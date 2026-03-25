import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import './App.css'

type SessionSummary = {
  sessionId: string
  agentId: number
  createdAt: string
  messagesCount: number
}

type SessionHistoryMessage = {
  role: string
  text: string | null
  authorName: string | null
  runId: string | null
  runStatus: RunStatus | null
}

type SessionHistoryResponse = {
  sessionId: string
  activeRunId: string | null
  activeRunStatus: RunStatus | null
  messages: SessionHistoryMessage[]
}

type RunStatus = 'pending' | 'running' | 'completed' | 'failed'

type StreamEvent = {
  runId: string
  sessionId: string
  sequence: number
  type: 'started' | 'delta' | 'completed' | 'failed'
  text: string | null
  timestamp: string
  status: RunStatus
}

type ChatBubble = {
  id: string
  role: 'user' | 'assistant' | 'system'
  text: string
  isStreaming?: boolean
  runId?: string | null
}

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'https://localhost:7063'

function App() {
  const [agentId, setAgentId] = useState<number>(1)
  const [sessions, setSessions] = useState<SessionSummary[]>([])
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<ChatBubble[]>([])
  const [prompt, setPrompt] = useState('')
  const [isSending, setIsSending] = useState(false)
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

  useEffect(() => {
    void refreshSessions(agentId)
  }, [agentId])

  useEffect(() => {
    return () => {
      closeStream()
    }
  }, [])

  async function fetchJson<T>(url: string, init?: RequestInit): Promise<T> {
    const response = await fetch(url, {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        ...(init?.headers ?? {}),
      },
    })

    if (!response.ok) {
      const body = await response.text()
      throw new Error(body || `Request failed with status ${response.status}`)
    }

    return (await response.json()) as T
  }

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

      const mapped: ChatBubble[] = data.messages
        .map((message, index) => ({
          id: `${sessionId}-${index}`,
          role: normalizeRole(message.role),
          text: message.text ?? '',
          runId: message.runId,
        }))
        .filter((message) => message.role !== 'system')

      let assistantMessageId: string | null = null
      const hasActiveRun = data.activeRunId !== null && (data.activeRunStatus === 'pending' || data.activeRunStatus === 'running')

      if (hasActiveRun) {
        assistantMessageId =
          mapped.findLast((message) => message.role === 'assistant' && message.runId === data.activeRunId)?.id ?? null

        if (!assistantMessageId) {
          assistantMessageId = crypto.randomUUID()
          mapped.push({
            id: assistantMessageId,
            role: 'assistant',
            text: '',
            isStreaming: true,
            runId: data.activeRunId,
          })
        } else {
          for (const message of mapped) {
            if (message.id === assistantMessageId) {
              message.isStreaming = true
              break
            }
          }
        }
      }

      setMessages(mapped)

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

  return (
    <div className="app-shell">
      <aside className="sessions-panel">
        <div className="panel-header">
          <h1>SharpClaw</h1>
          <p>Agent Console</p>
        </div>

        <label className="field-label" htmlFor="agent-id-input">
          Agent ID
        </label>
        <input
          id="agent-id-input"
          className="text-input"
          type="number"
          min={1}
          value={agentId}
          onChange={(e) => setAgentId(Number(e.target.value || 1))}
        />

        <div className="sessions-actions">
          <button type="button" className="button primary" onClick={() => void createSession()}>
            New Session
          </button>
          <button type="button" className="button ghost" onClick={() => void refreshSessions(agentId)}>
            Refresh
          </button>
        </div>

        <div className="sessions-list">
          {sessions.map((session) => {
            const isSelected = selectedSessionId === session.sessionId
            return (
              <button
                key={session.sessionId}
                type="button"
                className={`session-card ${isSelected ? 'selected' : ''}`}
                onClick={() => {
                  setSelectedSessionId(session.sessionId)
                  void loadHistory(session.sessionId)
                }}
              >
                <div className="session-id">{session.sessionId.slice(0, 8)}</div>
                <div className="session-meta">
                  <span>{new Date(session.createdAt).toLocaleString()}</span>
                  <span>{session.messagesCount} msgs</span>
                </div>
              </button>
            )
          })}

          {sessions.length === 0 && <div className="empty-state">No sessions yet.</div>}
        </div>
      </aside>

      <main className="chat-panel">
        <header className="chat-header">
          <div>
            <h2>{selectedSession ? `Session ${selectedSession.sessionId.slice(0, 8)}` : 'No Session Selected'}</h2>
            <p>{selectedSession ? `Agent ${selectedSession.agentId}` : 'Create a session to start chatting.'}</p>
          </div>
          <code>{API_BASE_URL}</code>
        </header>

        <section className="messages-view">
          {messages
            .filter((message) => message.role !== 'system')
            .map((message) => (
              <article key={message.id} className={`bubble ${message.role}`}>
                <div className="bubble-role">{message.role}</div>
                <div className="bubble-text">{message.text || (message.isStreaming ? '...' : '')}</div>
              </article>
            ))}

          {messages.length === 0 && <div className="empty-state">No messages in this session.</div>}
        </section>

        <form className="composer" onSubmit={sendMessage}>
          <textarea
            className="composer-input"
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            placeholder="Write a message to the agent..."
            rows={3}
            disabled={isSessionProcessing}
          />
          <div className="composer-footer">
            {error && <span className="error-text">{error}</span>}
            <button
              type="submit"
              className="button primary"
              disabled={isSending || isSessionProcessing || prompt.trim().length === 0}
            >
              {isSending ? 'Sending...' : isSessionProcessing ? 'Processing...' : 'Send'}
            </button>
          </div>
        </form>
      </main>
    </div>
  )
}

function normalizeRole(role: string): 'user' | 'assistant' | 'system' {
  if (role === 'user' || role === 'assistant' || role === 'system') {
    return role
  }

  return 'assistant'
}

function asErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message
  }

  return 'Unknown error.'
}

export default App
