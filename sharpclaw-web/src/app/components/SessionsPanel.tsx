import type { AgentConfig, SessionSummary } from '../types/chat'

type SessionsPanelProps = {
  agents: AgentConfig[]
  selectedAgentId: number | null
  sessions: SessionSummary[]
  selectedSessionId: string | null
  showToolSessions: boolean
  unsavedSessionIds: Set<string>
  onSelectAgent: (agentId: number) => void
  onCreateSession: () => void
  onRefreshSessions: () => void
  onSelectSession: (sessionId: string) => void
  onRenameSession: (sessionId: string) => void
  onShowToolSessionsChange: (nextValue: boolean) => void
}

export function SessionsPanel({
  agents,
  selectedAgentId,
  sessions,
  selectedSessionId,
  showToolSessions,
  unsavedSessionIds,
  onSelectAgent,
  onCreateSession,
  onRefreshSessions,
  onSelectSession,
  onRenameSession,
  onShowToolSessionsChange,
}: SessionsPanelProps) {
  const visibleSessions = showToolSessions
    ? sessions
    : sessions.filter((session) => session.visibleInSidebar || session.sessionId === selectedSessionId)

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

      <label className="tool-toggle">
        <input
          type="checkbox"
          checked={showToolSessions}
          onChange={(event) => onShowToolSessionsChange(event.target.checked)}
        />
        Show tool sessions
      </label>

      <div className="sessions-list">
        {visibleSessions.map((session) => {
          const isSelected = selectedSessionId === session.sessionId
          const displayName = session.name?.trim() ? session.name : `Session ${session.sessionId.slice(0, 8)}`
          return (
            <div key={session.sessionId} className={`session-card ${isSelected ? 'selected' : ''}`}>
              <button
                type="button"
                className="session-card-selected"
                onClick={() => onSelectSession(session.sessionId)}
              >
                <div className="session-id">{displayName}</div>
              </button>
              <div className="session-meta">
                <span>{new Date(session.updatedAt).toLocaleString()}</span>
                <span>
                  {session.messagesCount} msgs{unsavedSessionIds.has(session.sessionId) ? ' *' : ''}
                </span>
              </div>
              <button type="button" className="button ghost" onClick={() => onRenameSession(session.sessionId)}>
                Rename
              </button>
            </div>
          )
        })}

        {visibleSessions.length === 0 && <div className="empty-state">No sessions yet.</div>}
      </div>
    </aside>
  )
}
