import type { ChatBubble, SessionHistoryMessage, SessionMessageContent } from '../types/chat'

function normalizeRole(role: string): 'user' | 'assistant' | 'system' {
  if (role === 'user' || role === 'assistant' || role === 'system') {
    return role
  }

  return 'assistant'
}

export function asErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message
  }

  return 'Unknown error.'
}

export function formatToolPayload(value: unknown): string {
  if (value === null || value === undefined) {
    return '(empty)'
  }

  if (typeof value === 'string') {
    const trimmed = value.trim()
    if (!trimmed) {
      return '(empty)'
    }

    try {
      const parsed = JSON.parse(trimmed)
      return JSON.stringify(parsed, null, 2)
    } catch {
      return value
    }
  }

  try {
    return JSON.stringify(value, null, 2)
  } catch {
    return String(value)
  }
}

export function formatToolResult(value: unknown): { text: string; format: 'text' | 'structured' } {
  if (value === null || value === undefined) {
    return { text: '(empty)', format: 'text' }
  }

  if (typeof value === 'string') {
    const trimmed = value.trim()
    if (!trimmed) {
      return { text: '(empty)', format: 'text' }
    }

    try {
      const parsed = JSON.parse(trimmed)

      if (parsed === null || parsed === undefined) {
        return { text: '(empty)', format: 'text' }
      }

      if (typeof parsed === 'string') {
        return { text: parsed, format: 'text' }
      }

      if (typeof parsed === 'object') {
        return { text: JSON.stringify(parsed, null, 2), format: 'structured' }
      }

      return { text: String(parsed), format: 'text' }
    } catch {
      return { text: value, format: 'text' }
    }
  }

  try {
    return { text: JSON.stringify(value, null, 2), format: 'structured' }
  } catch {
    return { text: String(value), format: 'text' }
  }
}

export function mapHistoryMessageToBubbles(
  sessionId: string,
  message: SessionHistoryMessage,
): ChatBubble[] {
  const role = normalizeRole(message.role)
  const contents = message.contents ?? []

  if (contents.length > 0) {
    const bubbles = contents
      .map((content, contentIndex) => mapHistoryContentToBubble(sessionId, message.messageId, contentIndex, role, content))
      .filter((bubble): bubble is ChatBubble => bubble !== null)

    if (bubbles.length > 0) {
      return bubbles
    }
  }

  if (!message.text) {
    return []
  }

  return [
    {
      id: `${sessionId}-${message.messageId}`,
      role,
      text: message.text,
      messageId: message.messageId,
    },
  ]
}

function mapHistoryContentToBubble(
  sessionId: string,
  messageId: number,
  contentIndex: number,
  role: 'user' | 'assistant' | 'system',
  content: SessionMessageContent,
): ChatBubble | null {
  const id = `${sessionId}-${messageId}-${contentIndex}`

  if (content.type === 'text') {
    if (!content.text) {
      return null
    }

    return {
      id,
      role,
      text: content.text,
      messageId,
    }
  }

  if (content.type === 'tool_call') {
    return {
      id,
      role: 'tool',
      text: '',
      messageId,
      toolEventType: 'tool_call',
      toolCallId: content.callId ?? null,
      toolName: content.toolName ?? null,
      toolArguments: formatToolPayload(content.arguments ?? null),
      toolExpanded: false,
      toolResultExpanded: false,
    }
  }

  if (content.type === 'tool_result') {
    const formattedResult = formatToolResult(content.result ?? null)
    const childSessionId = extractChildSessionId(content.result ?? null)
    return {
      id,
      role: 'tool',
      text: '',
      messageId,
      toolEventType: 'tool_result',
      toolCallId: content.callId ?? null,
      toolResult: formattedResult.text,
      toolResultFormat: formattedResult.format,
      toolExpanded: false,
      childSessionId,
      childSessionCompleted: extractChildSessionCompleted(content.result ?? null),
    }
  }

  if (!content.payload) {
    return null
  }

  const formattedPayload = formatToolResult(content.payload)
  return {
    id,
    role: 'tool',
    text: '',
    messageId,
    toolEventType: 'tool_result',
    toolResult: formattedPayload.text,
    toolResultFormat: formattedPayload.format,
    toolExpanded: false,
  }
}

export function mergeToolResultBubbles(bubbles: ChatBubble[]): ChatBubble[] {
  return bubbles.reduce<ChatBubble[]>((acc, bubble) => {
    if (bubble.role === 'tool' && bubble.toolEventType === 'tool_result') {
      return mergeToolResultBubble(acc, bubble)
    }

    acc.push(bubble)
    return acc
  }, [])
}

export function mergeToolResultBubble(messages: ChatBubble[], resultBubble: ChatBubble): ChatBubble[] {
  const callId = resultBubble.toolCallId
  if (!callId) {
    return [...messages, resultBubble]
  }

  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const candidate = messages[index]
    if (
      candidate.role === 'tool' &&
      candidate.toolEventType === 'tool_call' &&
      candidate.toolCallId === callId
    ) {
      if (candidate.toolResult === resultBubble.toolResult) {
        return messages
      }

      const next = [...messages]
      next[index] = {
        ...candidate,
        toolResult: resultBubble.toolResult ?? '(empty)',
        toolResultFormat: resultBubble.toolResultFormat ?? 'text',
        toolResultExpanded: false,
        childSessionId: resultBubble.childSessionId ?? candidate.childSessionId ?? null,
        childSessionCompleted: resultBubble.childSessionCompleted ?? candidate.childSessionCompleted,
      }
      return next
    }

    if (
      candidate.role === 'tool' &&
      candidate.toolEventType === 'tool_result' &&
      candidate.toolCallId === callId &&
      candidate.toolResult === resultBubble.toolResult
    ) {
      return messages
    }
  }

  // Fallback for cases where history/stream contains only the result payload:
  // show it as a synthetic call card so UI still exposes the tool invocation.
  return [
    ...messages,
    {
      ...resultBubble,
      toolEventType: 'tool_call',
      toolName: resultBubble.toolName ?? 'unknown',
      toolArguments: '(not available)',
      toolExpanded: false,
      toolResultExpanded: false,
    },
  ]
}

export function attachChildSessionsToBubbles(
  bubbles: ChatBubble[],
  childSessions: { callId: string; childSessionId: string; completed: boolean }[],
): ChatBubble[] {
  if (childSessions.length === 0) {
    return bubbles
  }

  const byCallId = new Map(childSessions.map((x) => [x.callId, x] as const))
  return bubbles.map((bubble) => {
    if (bubble.role !== 'tool' || !bubble.toolCallId) {
      return bubble
    }

    const link = byCallId.get(bubble.toolCallId)
    if (!link) {
      return bubble
    }

    return {
      ...bubble,
      childSessionId: link.childSessionId,
      childSessionCompleted: link.completed,
    }
  })
}

export function extractTaskChildSessionMeta(result: unknown): {
  childSessionId: string | null
  completed: boolean | undefined
} {
  return {
    childSessionId: extractChildSessionId(result),
    completed: extractChildSessionCompleted(result),
  }
}

export function shortenCallId(callId: string): string {
  return callId.length > 12 ? callId.slice(0, 12) : callId
}

export function summarizeInline(value: string | null | undefined): string {
  if (!value) {
    return '(empty)'
  }

  const compact = value.replace(/\s+/g, ' ').trim()
  if (!compact) {
    return '(empty)'
  }

  return compact.length > 60 ? `${compact.slice(0, 60)}...` : compact
}

export function summarizeJsonInline(value: string | null | undefined): string {
  if (!value) {
    return 'args: (empty)'
  }

  try {
    const parsed = JSON.parse(value)
    return `args: ${JSON.stringify(parsed)}`
  } catch {
    const compact = value.replace(/\s+/g, ' ').trim()
    return `args: ${compact || '(empty)'}`
  }
}

function extractChildSessionId(result: unknown): string | null {
  const value = parseJsonLike(result)
  if (value && typeof value === 'object') {
    const id = (value as Record<string, unknown>).child_session_id
    return typeof id === 'string' && id.trim() ? id : null
  }

  return null
}

function extractChildSessionCompleted(result: unknown): boolean | undefined {
  const value = parseJsonLike(result)
  if (value && typeof value === 'object') {
    const status = (value as Record<string, unknown>).status
    if (typeof status === 'string') {
      return status === 'completed'
    }
  }

  return undefined
}

function parseJsonLike(value: unknown): unknown {
  if (typeof value !== 'string') {
    return value
  }

  const trimmed = value.trim()
  if (!trimmed) {
    return null
  }

  try {
    return JSON.parse(trimmed)
  } catch {
    return value
  }
}
