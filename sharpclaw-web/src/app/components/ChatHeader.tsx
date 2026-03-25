import type { SessionSummary } from '../types/chat'

type ChatHeaderProps = {
  selectedSession: SessionSummary | null
  showToolEvents: boolean
  apiBaseUrl: string
  onShowToolEventsChange: (nextValue: boolean) => void
}

export function ChatHeader({ selectedSession, showToolEvents, apiBaseUrl, onShowToolEventsChange }: ChatHeaderProps) {
  return (
    <header className="chat-header">
      <div>
        <h2>{selectedSession ? `Session ${selectedSession.sessionId.slice(0, 8)}` : 'No Session Selected'}</h2>
        <p>{selectedSession ? `Agent ${selectedSession.agentId}` : 'Create a session to start chatting.'}</p>
      </div>
      <div className="chat-controls">
        <label className="tool-toggle">
          <input type="checkbox" checked={showToolEvents} onChange={(event) => onShowToolEventsChange(event.target.checked)} />
          Show tool calls/results
        </label>
        <code>{apiBaseUrl}</code>
      </div>
    </header>
  )
}
