import type { AgentConfig, SessionSummary } from '../types/chat'

type SessionsPanelProps = {
  agents: AgentConfig[]
  selectedAgentId: number | null
  sessions: SessionSummary[]
  selectedSessionId: string | null
  unsavedSessionIds: Set<string>
  onSelectAgent: (agentId: number) => void
  onCreateSession: () => void
  onRefreshSessions: () => void
  onSelectSession: (sessionId: string) => void
}

export function SessionsPanel({
  agents,
  selectedAgentId,
  sessions,
  selectedSessionId,
  unsavedSessionIds,
  onSelectAgent,
  onCreateSession,
  onRefreshSessions,
  onSelectSession,
}: SessionsPanelProps) {
  return (
    <aside className="sessions-panel">
      <div className="panel-header">
        <h1>SharpClaw</h1>
        <p>Session Console</p>
      </div>

      <label className="field-label" htmlFor="agent-select">
        Agent
      </label>
      <select
        id="agent-select"
        className="text-input"
        value={selectedAgentId ?? ''}
        onChange={(event) => onSelectAgent(Number(event.target.value))}
      >
        {agents.map((agent) => (
          <option key={agent.id} value={agent.id}>
            {agent.id} - {agent.name}
          </option>
        ))}
      </select>

      <div className="sessions-actions">
        <button type="button" className="button primary" onClick={onCreateSession} disabled={!selectedAgentId}>
          New Session
        </button>
        <button type="button" className="button ghost" onClick={onRefreshSessions} disabled={!selectedAgentId}>
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
                <span>
                  {session.messagesCount} msgs{unsavedSessionIds.has(session.sessionId) ? ' *' : ''}
                </span>
              </div>
            </button>
          )
        })}

        {sessions.length === 0 && <div className="empty-state">No sessions yet.</div>}
      </div>
    </aside>
  )
}
