import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Composer } from '../components/Composer'
import { ChatHeader } from '../components/ChatHeader'
import { MessagesView } from '../components/MessagesView'
import { SessionsPanel } from '../components/SessionsPanel'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import type {
  AgentConfig,
  ChatBubble,
  ChildSessionSpawnedEventData,
  RunStatus,
  SessionHistoryResponse,
  SessionSummary,
  StreamEvent,
  ToolCallEventData,
  ToolResultEventData,
} from '../types/chat'
import {
  asErrorMessage,
  attachChildSessionsToBubbles,
  extractTaskChildSessionMeta,
  formatToolPayload,
  formatToolResult,
  mapHistoryMessageToBubbles,
  mergeToolResultBubble,
  mergeToolResultBubbles,
} from '../utils/chatUtils'
import './AgentConsolePage.css'

type AgentConsolePageProps = {
  onUnsavedChange?: (hasUnsaved: boolean) => void
}

export function AgentConsolePage({ onUnsavedChange }: AgentConsolePageProps) {
  const [agents, setAgents] = useState<AgentConfig[]>([])
  const [selectedAgentId, setSelectedAgentId] = useState<number | null>(null)
  const [sessions, setSessions] = useState<SessionSummary[]>([])
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null)
  const [showToolSessions, setShowToolSessions] = useState(false)
  const [messages, setMessages] = useState<ChatBubble[]>([])
  const [draftsBySessionKey, setDraftsBySessionKey] = useState<Record<string, string>>({})
  const [sendingSessionIds, setSendingSessionIds] = useState<Set<string>>(new Set())
  const [showToolEvents, setShowToolEvents] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [activeRun, setActiveRun] = useState<{ sessionId: string; status: RunStatus } | null>(null)
  const [currentParentSessionId, setCurrentParentSessionId] = useState<string | null>(null)
  const [pendingApproval, setPendingApproval] = useState<{
    token: string
    action: string
    target: string | null
    commandPreview: string | null
    risk: string
    description: string
  } | null>(null)
  const streamRef = useRef<{ sessionId: string; latestMessageId: number; source: EventSource } | null>(null)
  const draftSessionKey = selectedSessionId ?? (selectedAgentId !== null ? `__new__:${selectedAgentId}` : '__new__:none')
  const prompt = draftsBySessionKey[draftSessionKey] ?? ''
  const unsavedSessionIds = useMemo(() => {
    const next = new Set<string>()
    for (const [sessionKey, draft] of Object.entries(draftsBySessionKey)) {
      if (!draft.trim()) {
        continue
      }
      if (sessionKey.startsWith('__new__:')) {
        continue
      }
      next.add(sessionKey)
    }
    return next
  }, [draftsBySessionKey])
  const hasUnsavedDrafts = useMemo(
    () => Object.values(draftsBySessionKey).some((draft) => draft.trim().length > 0),
    [draftsBySessionKey],
  )

  const selectedSession = useMemo(
    () => sessions.find((session) => session.sessionId === selectedSessionId) ?? null,
    [selectedSessionId, sessions],
  )
  const isSessionProcessing =
    activeRun !== null &&
    selectedSessionId !== null &&
    activeRun.sessionId === selectedSessionId &&
    (activeRun.status === 'pending' || activeRun.status === 'waiting' || activeRun.status === 'running')
  const isSessionSending = selectedSessionId !== null && sendingSessionIds.has(selectedSessionId)
  const visibleMessages = messages.filter(
    (message) => message.role !== 'system' && (showToolEvents || message.role !== 'tool' || Boolean(message.childSessionId)),
  )
  const latestMessageId = messages.length > 0 ? messages[messages.length - 1].messageId : -1

  useEffect(() => {
    void loadAgents()
  }, [])

  useEffect(() => {
    if (selectedAgentId === null) {
      setSessions([])
      setSelectedSessionId(null)
      setMessages([])
      return
    }

    void refreshSessions(selectedAgentId)
  }, [selectedAgentId])

  useEffect(() => {
    return () => {
      closeStream()
    }
  }, [])

  useEffect(() => {
    onUnsavedChange?.(hasUnsavedDrafts)
  }, [hasUnsavedDrafts, onUnsavedChange])

  async function loadAgents() {
    try {
      setError(null)
      const data = await fetchJson<{ agents: AgentConfig[] }>(`${API_BASE_URL}/agents`)
      setAgents(data.agents)

      if (data.agents.length === 0) {
        setSelectedAgentId(null)
        return
      }

      const current = selectedAgentId !== null ? data.agents.find((agent) => agent.id === selectedAgentId) : null
      setSelectedAgentId((current ?? data.agents[0]).id)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function refreshSessions(currentAgentId: number, preferredSessionId?: string | null) {
    try {
      setError(null)
      const data = await fetchJson<{ agentId: number; sessions: SessionSummary[] }>(`${API_BASE_URL}/agents/${currentAgentId}/sessions`)
      setSessions(data.sessions)

      const nextSessionId =
        (preferredSessionId && data.sessions.some((session) => session.sessionId === preferredSessionId)
          ? preferredSessionId
          : null) ??
        (selectedSessionId && data.sessions.some((session) => session.sessionId === selectedSessionId) ? selectedSessionId : null) ??
        data.sessions[0]?.sessionId ??
        null

      if (!nextSessionId) {
        setSelectedSessionId(null)
        setCurrentParentSessionId(null)
        setMessages([])
        return
      }

      if (nextSessionId !== selectedSessionId || preferredSessionId) {
        setSelectedSessionId(nextSessionId)
        await loadHistory(nextSessionId)
      }
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function createSession() {
    if (selectedAgentId === null) {
      return
    }

    try {
      setError(null)
      const providedName = window.prompt('Session name (optional):')?.trim() ?? ''
      const data = await fetchJson<{ sessionId: string }>(`${API_BASE_URL}/sessions`, {
        method: 'POST',
        body: JSON.stringify({
          agentId: selectedAgentId,
          name: providedName.length > 0 ? providedName : undefined,
        }),
      })

      await refreshSessions(selectedAgentId, data.sessionId)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function loadHistory(sessionId: string) {
    try {
      setError(null)
      closeStream()
      const data = await fetchJson<SessionHistoryResponse>(`${API_BASE_URL}/sessions/${sessionId}/history`)
      setCurrentParentSessionId(data.parentSessionId ?? null)

      const mapped: ChatBubble[] = data.messages.flatMap((message) =>
        mapHistoryMessageToBubbles(sessionId, message),
      )
      const mergedMapped = attachChildSessionsToBubbles(
        mergeToolResultBubbles(mapped),
        data.childSessions ?? [],
      )

      let assistantMessageId: string | null = null
      const hasActiveRun = (data.runStatus === 'pending' || data.runStatus === 'waiting' || data.runStatus === 'running')

      if (hasActiveRun) {
        assistantMessageId =
          mergedMapped.findLast((message) => message.role === 'assistant' && message.messageId === data.latestSequenceId)?.id ?? null

        if (!assistantMessageId) {
          assistantMessageId = crypto.randomUUID()
          mergedMapped.push({
            id: assistantMessageId,
            role: 'assistant',
            text: '',
            isStreaming: true,
            messageId: data.latestSequenceId + 1,
          })
        } else {
          for (const message of mergedMapped) {
            if (message.id === assistantMessageId) {
              message.isStreaming = true
              break
            }
          }
        }
      }

      setMessages(mergedMapped)

      if (hasActiveRun && data.runStatus && assistantMessageId) {
        setActiveRun({ sessionId, status: data.runStatus })
        void streamRun(sessionId, data.latestSequenceId, assistantMessageId)
          .then(async () => {
            await loadHistory(sessionId)
            if (selectedAgentId !== null) {
              await refreshSessions(selectedAgentId, sessionId)
            }
          })
          .catch((e) => setError(asErrorMessage(e)))
      } else {
        setActiveRun(null)
      }
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function sendMessage(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (selectedAgentId === null) {
      setError('Select an agent first.')
      return
    }

    const text = prompt.trim()
    if (!text || isSessionSending || isSessionProcessing) {
      return
    }

    let sessionId: string | null = null
    try {
      setError(null)
      const sourceDraftSessionKey = draftSessionKey

      sessionId = selectedSessionId
      if (!sessionId) {
        const created = await fetchJson<{ sessionId: string }>(`${API_BASE_URL}/sessions`, {
          method: 'POST',
          body: JSON.stringify({ agentId: selectedAgentId }),
        })
        sessionId = created.sessionId
        setSelectedSessionId(sessionId)
        await refreshSessions(selectedAgentId, sessionId)
      }
      if (!sessionId) {
        throw new Error('Session could not be created.')
      }
      const currentSessionId = sessionId

      setSendingSessionIds((prev) => {
        const next = new Set(prev)
        next.add(currentSessionId)
        return next
      })

      const localUserId = crypto.randomUUID()
      const localAssistantId = crypto.randomUUID()

      setMessages((prev) => [
        ...prev,
        {
          id: localUserId,
          role: 'user',
          text,
          messageId: latestMessageId + 1,
        },
        // { id: localAssistantId, role: 'assistant', text: '', isStreaming: true },
      ])

      // TODO: errors?
      await fetchJson<{ runId: string }>(`${API_BASE_URL}/sessions/${currentSessionId}/messages`, {
        method: 'POST',
        body: JSON.stringify({ message: text }),
      })
      setDraftsBySessionKey((prev) =>
        prev[sourceDraftSessionKey] === undefined ? prev : { ...prev, [sourceDraftSessionKey]: '' },
      )

      setActiveRun({ sessionId: currentSessionId, status: 'pending' })
      void streamRun(currentSessionId, latestMessageId + 1, localAssistantId)
        .then(async () => {
          await loadHistory(currentSessionId)
          await refreshSessions(selectedAgentId, currentSessionId)
        })
        .catch((streamError) => {
          setError(asErrorMessage(streamError))
          setMessages((prev) => prev.map((m) => (m.isStreaming ? { ...m, isStreaming: false } : m)))
        })
    } catch (e) {
      setError(asErrorMessage(e))
      setMessages((prev) => prev.map((m) => (m.isStreaming ? { ...m, isStreaming: false } : m)))
    } finally {
      if (sessionId) {
        const sessionToClear = sessionId
        setSendingSessionIds((prev) => {
          const next = new Set(prev)
          next.delete(sessionToClear)
          return next
        })
      }
    }
  }

  async function renameSession(sessionId: string) {
    const current = sessions.find((s) => s.sessionId === sessionId)
    const nextName = window.prompt('Rename session:', current?.name ?? '')?.trim()
    if (!nextName) {
      return
    }

    try {
      setError(null)
      await fetchJson<{ sessionId: string; name: string }>(`${API_BASE_URL}/sessions/${sessionId}`, {
        method: 'PATCH',
        body: JSON.stringify({ name: nextName }),
      })
      if (selectedAgentId !== null) {
        await refreshSessions(selectedAgentId, sessionId)
      }
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  function streamRun(sessionId: string, latestMessageId: number, assistantMessageId: string) {
    return new Promise<void>((resolve, reject) => {
      closeStream()

      const streamUrl = `${API_BASE_URL}/sessions/${sessionId}/messages/${latestMessageId}/stream`
      const source = new EventSource(streamUrl)
      streamRef.current = { sessionId, latestMessageId, source }

      const close = () => {
        source.close()
        if (streamRef.current?.latestMessageId === latestMessageId && streamRef.current?.sessionId === sessionId) {
          streamRef.current = null
        }
      }

      source.onerror = () => {
        close()
        setActiveRun(null)
        reject(new Error('Streaming connection closed unexpectedly.'))
      }

      source.addEventListener('started', (event) => {
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        setActiveRun({ sessionId, status: payload.status })
      })

      source.addEventListener('delta', (event) => {
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        const delta = payload.text ?? ''
        if (!delta) {
          return
        }

        setActiveRun({ sessionId, status: payload.status })

        setMessages((prev) =>
        {
          if (prev.some((message) => message.messageId === payload.messageId)) {
            return prev.map((message) =>
                message.messageId === payload.messageId
                    ? {
                      ...message,
                      text: `${message.text}${delta}`,
                      isStreaming: true,
                    }
                    : message,
            )
          } else {
            return [
              ...prev,
              {
                id: crypto.randomUUID(),
                role: 'assistant',
                text: delta,
                messageId: payload.messageId,
              }
            ]
          }
        }
        )
      })

      source.addEventListener('tool_call', (event) => {
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        const data = payload.data as ToolCallEventData | undefined

        setActiveRun({ sessionId, status: payload.status })

        setMessages((prev) => [
          ...prev,
          {
            id: crypto.randomUUID(),
            role: 'tool',
            text: '',
            messageId: payload.messageId,
            toolEventType: 'tool_call',
            toolCallId: data?.callId ?? null,
            toolName: data?.toolName ?? null,
            toolArguments: formatToolPayload(data?.arguments ?? null),
            toolExpanded: false,
            toolResultExpanded: false,
          },
        ])
      })

      source.addEventListener('tool_result', (event) => {
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        const data = payload.data as ToolResultEventData | undefined

        setActiveRun({ sessionId, status: payload.status })
        const formattedResult = formatToolResult(data?.result ?? null)
        const childMeta = extractTaskChildSessionMeta(data?.result ?? null)

        const resultBubble: ChatBubble = {
          id: crypto.randomUUID(),
          role: 'tool',
          text: '',
          messageId: payload.messageId,
          toolEventType: 'tool_result',
          toolCallId: data?.callId ?? null,
          toolResult: formattedResult.text,
          toolResultFormat: formattedResult.format,
          toolExpanded: false,
          childSessionId: childMeta.childSessionId,
          childSessionCompleted: childMeta.completed,
        }

        setMessages((prev) => mergeToolResultBubble(prev, resultBubble))
      })

      source.addEventListener('child_session_spawned', (event) => {
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        const data = payload.data as ChildSessionSpawnedEventData | undefined
        const childSessionId = data?.childSessionId ?? null
        const callId = data?.callId ?? null
        if (!childSessionId) {
          return
        }

        setActiveRun({ sessionId, status: payload.status })
        setMessages((prev) =>
          prev.map((message) =>
            message.role === 'tool' && message.toolCallId === callId
              ? {
                  ...message,
                  childSessionId,
                  childSessionCompleted: false,
                }
              : message,
          ),
        )
      })

      source.addEventListener('completed', () => {
        close()
        setActiveRun(null)
        setMessages((prev) =>
          prev.map((message) =>
            message.id === assistantMessageId
              ? {
                  ...message,
                  isStreaming: false,
                }
              : message,
          ),
        )
        resolve()
      })

      source.addEventListener('failed', (event) => {
        close()
        setActiveRun(null)
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        reject(new Error(payload.text || 'Run failed.'))
      })

      source.addEventListener('approval_required', (event) => {
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        const data = payload.data as { approval_token: string; action: string; target: string | null; command_preview: string | null; risk: string; description: string } | undefined
        if (data) {
          setPendingApproval({
            token: data.approval_token,
            action: data.action,
            target: data.target,
            commandPreview: data.command_preview,
            risk: data.risk,
            description: data.description,
          })
        }
      })
    })
  }

  async function resolveApproval(approved: boolean) {
    if (!pendingApproval || !selectedSessionId) return
    try {
      setError(null)
      const method = approved ? 'approve' : 'reject'
      await fetchJson(
        `${API_BASE_URL}/sessions/${selectedSessionId}/approvals/${pendingApproval.token}/${method}`,
        { method: 'POST' },
      )
      setPendingApproval(null)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  function closeStream() {
    if (streamRef.current) {
      streamRef.current.source.close()
      streamRef.current = null
    }
  }

  function toggleToolExpanded(messageId: string) {
    setMessages((prev) =>
      prev.map((message) =>
        message.id === messageId && message.role === 'tool'
          ? {
              ...message,
              toolExpanded: !message.toolExpanded,
            }
          : message,
      ),
    )
  }

  function toggleToolResultExpanded(messageId: string) {
    setMessages((prev) =>
      prev.map((message) =>
        message.id === messageId && message.role === 'tool'
          ? {
              ...message,
              toolResultExpanded: !message.toolResultExpanded,
            }
          : message,
      ),
    )
  }

  return (
    <div className="app-shell">
      <SessionsPanel
        agents={agents}
        selectedAgentId={selectedAgentId}
        sessions={sessions}
        selectedSessionId={selectedSessionId}
        showToolSessions={showToolSessions}
        unsavedSessionIds={unsavedSessionIds}
        onSelectAgent={(agentId) => {
          closeStream()
          setActiveRun(null)
          setSelectedSessionId(null)
          setMessages([])
          setSelectedAgentId(agentId)
        }}
        onCreateSession={() => void createSession()}
        onRefreshSessions={() => {
          if (selectedAgentId !== null) {
            void refreshSessions(selectedAgentId)
          }
        }}
        onSelectSession={(sessionId) => {
          setSelectedSessionId(sessionId)
          void loadHistory(sessionId)
        }}
        onRenameSession={(sessionId) => void renameSession(sessionId)}
        onShowToolSessionsChange={setShowToolSessions}
      />

      <main className="chat-panel">
        <ChatHeader
          selectedSession={selectedSession}
          parentSessionId={currentParentSessionId ?? selectedSession?.parentSessionId ?? null}
          hasUnsavedDraft={prompt.trim().length > 0}
          showToolEvents={showToolEvents}
          apiBaseUrl={API_BASE_URL}
          onShowToolEventsChange={setShowToolEvents}
          onGoToSession={(sessionId) => {
            setSelectedSessionId(sessionId)
            void loadHistory(sessionId)
          }}
          onError={(msg) => setError(msg)}
        />
        <MessagesView
          messages={visibleMessages}
          showToolEvents={showToolEvents}
          onToggleToolExpanded={toggleToolExpanded}
          onToggleToolResultExpanded={toggleToolResultExpanded}
          onOpenSession={(sessionId) => {
            setSelectedSessionId(sessionId)
            void loadHistory(sessionId)
          }}
        />
        <Composer
          prompt={prompt}
          isSending={isSessionSending}
          isSessionProcessing={isSessionProcessing}
          error={error}
          onPromptChange={(value) => {
            setDraftsBySessionKey((prev) => {
              if (prev[draftSessionKey] === value) {
                return prev
              }
              return {
                ...prev,
                [draftSessionKey]: value,
              }
            })
          }}
          onSubmit={sendMessage}
        />
      </main>

      {pendingApproval && (
        <div className="approval-overlay">
          <div className="approval-dialog">
            <div className="approval-dialog-header">
              <h3>Action Requires Approval</h3>
              <span className={`approval-risk-badge ${pendingApproval.risk}`}>{pendingApproval.risk}</span>
            </div>
            <div className="approval-dialog-body">
              <p className="approval-description">{pendingApproval.description}</p>
              {pendingApproval.target && (
                <div className="approval-detail">
                  <span>Path:</span>
                  <code>{pendingApproval.target}</code>
                </div>
              )}
              {pendingApproval.commandPreview && (
                <div className="approval-detail">
                  <span>Command:</span>
                  <code>{pendingApproval.commandPreview}</code>
                </div>
              )}
              <div className="approval-detail">
                <span>Action:</span>
                <span className="approval-action-type">{pendingApproval.action}</span>
              </div>
            </div>
            <div className="approval-dialog-footer">
              <button
                type="button"
                className="button ghost"
                onClick={() => void resolveApproval(false)}
              >
                Reject
              </button>
              <button
                type="button"
                className="button primary"
                onClick={() => void resolveApproval(true)}
              >
                Approve
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
