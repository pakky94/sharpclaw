import type { ChatBubble } from '../types/chat'
import { shortenCallId, summarizeInline, summarizeJsonInline } from '../utils/chatUtils'

type MessagesViewProps = {
  messages: ChatBubble[]
  onToggleToolExpanded: (messageId: string) => void
  onToggleToolResultExpanded: (messageId: string) => void
}

export function MessagesView({ messages, onToggleToolExpanded, onToggleToolResultExpanded }: MessagesViewProps) {
  return (
    <section className="messages-view">
      {messages.map((message) => (
        <article key={message.id} className={`bubble ${message.role}`}>
          {message.role === 'tool' ? (
            <>
              {message.toolEventType === 'tool_call' ? (
                <div className="tool-card">
                  <button type="button" className="tool-summary-button" onClick={() => onToggleToolExpanded(message.id)}>
                    <span className="tool-summary-main">
                      {message.toolExpanded ? '▼' : '▶'} call: {message.toolName ?? 'unknown'}
                      {message.toolCallId ? ` (${shortenCallId(message.toolCallId)})` : ''}
                    </span>
                    {!message.toolExpanded && (
                      <span className="tool-summary-inline">{summarizeJsonInline(message.toolArguments)}</span>
                    )}
                  </button>

                  {message.toolExpanded && (
                    <div className="tool-details">
                      <div className="bubble-meta">call id: {message.toolCallId ?? 'n/a'}</div>
                      <pre className="tool-block">{message.toolArguments ?? '(empty)'}</pre>
                    </div>
                  )}

                  {message.toolResult && (
                    <div className="tool-result-section">
                      <button
                        type="button"
                        className="tool-summary-button result"
                        onClick={() => onToggleToolResultExpanded(message.id)}
                      >
                        {message.toolResultExpanded ? '▼' : '▶'} result: {summarizeInline(message.toolResult)}
                      </button>
                      {message.toolResultExpanded && <pre className="tool-block">{message.toolResult}</pre>}
                    </div>
                  )}
                </div>
              ) : (
                <div className="tool-card">
                  <button type="button" className="tool-summary-button result" onClick={() => onToggleToolExpanded(message.id)}>
                    {message.toolExpanded ? '▼' : '▶'} result
                    {message.toolCallId ? ` (${shortenCallId(message.toolCallId)})` : ''}: {summarizeInline(message.toolResult)}
                  </button>
                  {message.toolExpanded && (
                    <div className="tool-details">
                      {message.toolCallId && <div className="bubble-meta">call id: {message.toolCallId}</div>}
                      <pre className="tool-block">{message.toolResult ?? '(empty)'}</pre>
                    </div>
                  )}
                </div>
              )}
            </>
          ) : (
            <>
              <div className="bubble-role">{message.role}</div>
              <div className="bubble-text">{message.text || (message.isStreaming ? '...' : '')}</div>
            </>
          )}
        </article>
      ))}

      {messages.length === 0 && <div className="empty-state">No messages in this session.</div>}
    </section>
  )
}
