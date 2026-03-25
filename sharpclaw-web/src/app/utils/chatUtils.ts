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

export function mapHistoryMessageToBubbles(
  sessionId: string,
  messageIndex: number,
  message: SessionHistoryMessage,
): ChatBubble[] {
  const role = normalizeRole(message.role)
  const contents = message.contents ?? []

  if (contents.length > 0) {
    const bubbles = contents
      .map((content, contentIndex) => mapHistoryContentToBubble(sessionId, messageIndex, contentIndex, role, message.runId, content))
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
      id: `${sessionId}-${messageIndex}`,
      role,
      text: message.text,
      runId: message.runId,
    },
  ]
}

function mapHistoryContentToBubble(
  sessionId: string,
  messageIndex: number,
  contentIndex: number,
  role: 'user' | 'assistant' | 'system',
  runId: string | null,
  content: SessionMessageContent,
): ChatBubble | null {
  const id = `${sessionId}-${messageIndex}-${contentIndex}`

  if (content.type === 'text') {
    if (!content.text) {
      return null
    }

    return {
      id,
      role,
      text: content.text,
      runId,
    }
  }

  if (content.type === 'tool_call') {
    return {
      id,
      role: 'tool',
      text: '',
      runId,
      toolEventType: 'tool_call',
      toolCallId: content.callId ?? null,
      toolName: content.toolName ?? null,
      toolArguments: formatToolPayload(content.arguments ?? null),
      toolExpanded: false,
      toolResultExpanded: false,
    }
  }

  if (content.type === 'tool_result') {
    return {
      id,
      role: 'tool',
      text: '',
      runId,
      toolEventType: 'tool_result',
      toolCallId: content.callId ?? null,
      toolResult: formatToolPayload(content.result ?? null),
      toolExpanded: false,
    }
  }

  if (!content.payload) {
    return null
  }

  return {
    id,
    role: 'tool',
    text: '',
    runId,
    toolEventType: 'tool_result',
    toolResult: formatToolPayload(content.payload),
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
      candidate.toolCallId === callId &&
      candidate.runId === resultBubble.runId &&
      !candidate.toolResult
    ) {
      const next = [...messages]
      next[index] = {
        ...candidate,
        toolResult: resultBubble.toolResult ?? '(empty)',
        toolResultExpanded: false,
      }
      return next
    }
  }

  return [...messages, resultBubble]
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
