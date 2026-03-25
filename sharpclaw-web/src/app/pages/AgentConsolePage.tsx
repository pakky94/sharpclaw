import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Composer } from '../components/Composer'
import { ChatHeader } from '../components/ChatHeader'
import { MessagesView } from '../components/MessagesView'
import { SessionsPanel } from '../components/SessionsPanel'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import type {
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
  const [agentId, setAgentId] = useState<number>(1)
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
    void refreshSessions(agentId)
  }, [agentId])

  useEffect(() => {
    return () => {
      closeStream()
    }
  }, [])

  async function refreshSessions(currentAgentId: number) {
    try {
      setError(null)
      const data = await fetchJson<{ agentId: number; sessions: SessionSummary[] }>(
        `${API_BASE_URL}/agents/${currentAgentId}/sessions`,
      )
      setSessions(data.sessions)

      if (data.sessions.length > 0 && !selectedSessionId) {
        const first = data.sessions[0]
        setSelectedSessionId(first.sessionId)
        await loadHistory(first.sessionId)
      }

      if (data.sessions.length === 0) {
        setSelectedSessionId(null)
        setMessages([])
      }
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function createSession() {
    try {
      setError(null)
      const data = await fetchJson<{ sessionId: string }>(`${API_BASE_URL}/sessions`, {
        method: 'POST',
        body: JSON.stringify({ agentId }),
      })

      await refreshSessions(agentId)
      setSelectedSessionId(data.sessionId)
      await loadHistory(data.sessionId)
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
            await refreshSessions(agentId)
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
          body: JSON.stringify({ agentId }),
        })
        sessionId = created.sessionId
        setSelectedSessionId(sessionId)
        await refreshSessions(agentId)
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
      await refreshSessions(agentId)
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
        agentId={agentId}
        sessions={sessions}
        selectedSessionId={selectedSessionId}
        onAgentIdChange={setAgentId}
        onCreateSession={() => void createSession()}
        onRefresh={() => void refreshSessions(agentId)}
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
