import type { SessionSummary } from '../types/chat'

type SessionsPanelProps = {
  agentId: number
  sessions: SessionSummary[]
  selectedSessionId: string | null
  onAgentIdChange: (value: number) => void
  onCreateSession: () => void
  onRefresh: () => void
  onSelectSession: (sessionId: string) => void
}

export function SessionsPanel({
  agentId,
  sessions,
  selectedSessionId,
  onAgentIdChange,
  onCreateSession,
  onRefresh,
  onSelectSession,
}: SessionsPanelProps) {
  return (
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
        onChange={(e) => onAgentIdChange(Number(e.target.value || 1))}
      />

      <div className="sessions-actions">
        <button type="button" className="button primary" onClick={onCreateSession}>
          New Session
        </button>
        <button type="button" className="button ghost" onClick={onRefresh}>
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
              onClick={() => onSelectSession(session.sessionId)}
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
  )
}
