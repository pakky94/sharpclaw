import { useEffect, useRef } from 'react'
import type { ChatBubble } from '../types/chat'
import { MarkdownContent } from './MarkdownContent'
import { shortenCallId, summarizeInline, summarizeJsonInline } from '../utils/chatUtils'

type MessagesViewProps = {
  messages: ChatBubble[]
  showToolEvents: boolean
  onToggleToolExpanded: (messageId: string) => void
  onToggleToolResultExpanded: (messageId: string) => void
  onOpenSession: (sessionId: string) => void
}

export function MessagesView({
  messages,
  showToolEvents,
  onToggleToolExpanded,
  onToggleToolResultExpanded,
  onOpenSession,
}: MessagesViewProps) {
  const containerRef = useRef<HTMLElement | null>(null)
  const shouldAutoScrollRef = useRef(true)
  const prevMessageCountRef = useRef(0)
  const prevFirstMessageIdRef = useRef<string | null>(null)

  useEffect(() => {
    const container = containerRef.current
    if (!container) {
      return
    }

    const prevCount = prevMessageCountRef.current
    const firstMessageId = messages[0]?.id ?? null
    const isInitialLoad = prevCount === 0 && messages.length > 0
    const isNewSession = prevFirstMessageIdRef.current !== null && firstMessageId !== prevFirstMessageIdRef.current
    if (isInitialLoad || isNewSession || shouldAutoScrollRef.current) {
      container.scrollTop = container.scrollHeight
    }

    prevMessageCountRef.current = messages.length
    prevFirstMessageIdRef.current = firstMessageId
  }, [messages])

  function handleScroll() {
    const container = containerRef.current
    if (!container) {
      return
    }

    const threshold = 80
    const distanceFromBottom = container.scrollHeight - container.scrollTop - container.clientHeight
    shouldAutoScrollRef.current = distanceFromBottom < threshold
  }

  return (
    <section className="messages-view" ref={containerRef} onScroll={handleScroll}>
      {messages.map((message) => (
        <article key={message.id} className={`bubble ${message.role}`}>
          {message.role === 'tool' ? (
            <>
              {!showToolEvents && message.childSessionId ? (
                <div className="tool-card">
                  <button type="button" className="tool-link-button" onClick={() => onOpenSession(message.childSessionId!)}>
                    Open child session {message.childSessionId.slice(0, 8)}
                    {message.childSessionCompleted === false ? ' (running)' : ''}
                  </button>
                </div>
              ) : (
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
                      {message.childSessionId && (
                        <button type="button" className="tool-link-button" onClick={() => onOpenSession(message.childSessionId!)}>
                          Open child session {message.childSessionId.slice(0, 8)}
                          {message.childSessionCompleted === false ? ' (running)' : ''}
                        </button>
                      )}
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
                      {message.toolResultExpanded &&
                        (message.toolResultFormat === 'structured' ? (
                          <pre className="tool-block">{message.toolResult}</pre>
                        ) : (
                          <MarkdownContent className="tool-markdown markdown-content" content={message.toolResult ?? '(empty)'} />
                        ))}
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
                      {message.childSessionId && (
                        <button type="button" className="tool-link-button" onClick={() => onOpenSession(message.childSessionId!)}>
                          Open child session {message.childSessionId.slice(0, 8)}
                          {message.childSessionCompleted === false ? ' (running)' : ''}
                        </button>
                      )}
                      {message.toolResultFormat === 'structured' ? (
                        <pre className="tool-block">{message.toolResult ?? '(empty)'}</pre>
                      ) : (
                        <MarkdownContent className="tool-markdown markdown-content" content={message.toolResult ?? '(empty)'} />
                      )}
                    </div>
                  )}
                </div>
              )}
                </>
              )}
            </>
          ) : (
            <>
              <div className="bubble-role">{message.role}</div>
              <MarkdownContent className="bubble-text markdown-content" content={message.text || (message.isStreaming ? '...' : '')} />
            </>
          )}
        </article>
      ))}

      {messages.length === 0 && <div className="empty-state">No messages in this session.</div>}
    </section>
  )
}
