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
  RunStatus,
  SessionHistoryResponse,
  SessionSummary,
  StreamEvent,
  ToolCallEventData,
  ToolResultEventData,
} from '../types/chat'
import {
  asErrorMessage,
  formatToolPayload,
  formatToolResult,
  mapHistoryMessageToBubbles,
  mergeToolResultBubble,
  mergeToolResultBubbles,
} from '../utils/chatUtils'
import './AgentConsolePage.css'

export function AgentConsolePage() {
  const [agents, setAgents] = useState<AgentConfig[]>([])
  const [selectedAgentId, setSelectedAgentId] = useState<number | null>(null)
  const [sessions, setSessions] = useState<SessionSummary[]>([])
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<ChatBubble[]>([])
  const [prompt, setPrompt] = useState('')
  const [isSending, setIsSending] = useState(false)
  const [showToolEvents, setShowToolEvents] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [activeRun, setActiveRun] = useState<{ sessionId: string; runId: string; status: RunStatus } | null>(null)
  const streamRef = useRef<{ sessionId: string; runId: string; source: EventSource } | null>(null)

  const selectedSession = useMemo(
    () => sessions.find((session) => session.sessionId === selectedSessionId) ?? null,
    [selectedSessionId, sessions],
  )
  const isSessionProcessing =
    activeRun !== null &&
    selectedSessionId !== null &&
    activeRun.sessionId === selectedSessionId &&
    (activeRun.status === 'pending' || activeRun.status === 'running')
  const visibleMessages = messages.filter((message) => message.role !== 'system' && (showToolEvents || message.role !== 'tool'))

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
      const data = await fetchJson<{ sessionId: string }>(`${API_BASE_URL}/sessions`, {
        method: 'POST',
        body: JSON.stringify({ agentId: selectedAgentId }),
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

      const mapped: ChatBubble[] = data.messages.flatMap((message, index) =>
        mapHistoryMessageToBubbles(sessionId, index, message),
      )
      const mergedMapped = mergeToolResultBubbles(mapped)

      let assistantMessageId: string | null = null
      const hasActiveRun = data.activeRunId !== null && (data.activeRunStatus === 'pending' || data.activeRunStatus === 'running')

      if (hasActiveRun) {
        assistantMessageId =
          mergedMapped.findLast((message) => message.role === 'assistant' && message.runId === data.activeRunId)?.id ?? null

        if (!assistantMessageId) {
          assistantMessageId = crypto.randomUUID()
          mergedMapped.push({
            id: assistantMessageId,
            role: 'assistant',
            text: '',
            isStreaming: true,
            runId: data.activeRunId,
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

      if (hasActiveRun && data.activeRunId && data.activeRunStatus && assistantMessageId) {
        setActiveRun({ sessionId, runId: data.activeRunId, status: data.activeRunStatus })
        void streamRun(sessionId, data.activeRunId, assistantMessageId)
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
    if (!text || isSending || isSessionProcessing) {
      return
    }

    try {
      setError(null)
      setIsSending(true)
      setPrompt('')

      let sessionId = selectedSessionId
      if (!sessionId) {
        const created = await fetchJson<{ sessionId: string }>(`${API_BASE_URL}/sessions`, {
          method: 'POST',
          body: JSON.stringify({ agentId: selectedAgentId }),
        })
        sessionId = created.sessionId
        setSelectedSessionId(sessionId)
        await refreshSessions(selectedAgentId, sessionId)
      }

      const localUserId = crypto.randomUUID()
      const localAssistantId = crypto.randomUUID()

      setMessages((prev) => [
        ...prev,
        { id: localUserId, role: 'user', text },
        { id: localAssistantId, role: 'assistant', text: '', isStreaming: true },
      ])

      const run = await fetchJson<{ runId: string }>(`${API_BASE_URL}/sessions/${sessionId}/messages`, {
        method: 'POST',
        body: JSON.stringify({ message: text }),
      })

      setActiveRun({ sessionId, runId: run.runId, status: 'pending' })
      await streamRun(sessionId, run.runId, localAssistantId)
      await loadHistory(sessionId)
      await refreshSessions(selectedAgentId, sessionId)
    } catch (e) {
      setError(asErrorMessage(e))
      setMessages((prev) => prev.map((m) => (m.isStreaming ? { ...m, isStreaming: false } : m)))
    } finally {
      setIsSending(false)
    }
  }

  function streamRun(sessionId: string, runId: string, assistantMessageId: string) {
    return new Promise<void>((resolve, reject) => {
      closeStream()

      const streamUrl = `${API_BASE_URL}/sessions/${sessionId}/runs/${runId}/stream`
      const source = new EventSource(streamUrl)
      streamRef.current = { sessionId, runId, source }

      const close = () => {
        source.close()
        if (streamRef.current?.runId === runId && streamRef.current?.sessionId === sessionId) {
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
        setActiveRun({ sessionId, runId, status: payload.status })
      })

      source.addEventListener('delta', (event) => {
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        const delta = payload.text ?? ''
        if (!delta) {
          return
        }

        setActiveRun({ sessionId, runId, status: payload.status })

        setMessages((prev) =>
          prev.map((message) =>
            message.id === assistantMessageId
              ? {
                  ...message,
                  text: `${message.text}${delta}`,
                  isStreaming: true,
                }
              : message,
          ),
        )
      })

      source.addEventListener('tool_call', (event) => {
        const payload = JSON.parse((event as MessageEvent).data) as StreamEvent
        const data = payload.data as ToolCallEventData | undefined

        setActiveRun({ sessionId, runId, status: payload.status })

        setMessages((prev) => [
          ...prev,
          {
            id: crypto.randomUUID(),
            role: 'tool',
            text: '',
            runId,
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

        setActiveRun({ sessionId, runId, status: payload.status })
        const formattedResult = formatToolResult(data?.result ?? null)

        const resultBubble: ChatBubble = {
          id: crypto.randomUUID(),
          role: 'tool',
          text: '',
          runId,
          toolEventType: 'tool_result',
          toolCallId: data?.callId ?? null,
          toolResult: formattedResult.text,
          toolResultFormat: formattedResult.format,
          toolExpanded: false,
        }

        setMessages((prev) => mergeToolResultBubble(prev, resultBubble))
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
    })
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
      />

      <main className="chat-panel">
        <ChatHeader
          selectedSession={selectedSession}
          showToolEvents={showToolEvents}
          apiBaseUrl={API_BASE_URL}
          onShowToolEventsChange={setShowToolEvents}
        />
        <MessagesView
          messages={visibleMessages}
          onToggleToolExpanded={toggleToolExpanded}
          onToggleToolResultExpanded={toggleToolResultExpanded}
        />
        <Composer
          prompt={prompt}
          isSending={isSending}
          isSessionProcessing={isSessionProcessing}
          error={error}
          onPromptChange={setPrompt}
          onSubmit={sendMessage}
        />
      </main>
    </div>
  )
}
