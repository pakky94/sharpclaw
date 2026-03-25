export type SessionSummary = {
  sessionId: string
  agentId: number
  createdAt: string
  messagesCount: number
}

export type SessionHistoryMessage = {
  role: string
  text: string | null
  contents: SessionMessageContent[]
  authorName: string | null
  runId: string | null
  runStatus: RunStatus | null
}

export type SessionMessageContent = {
  type: string
  text?: string | null
  callId?: string | null
  toolName?: string | null
  arguments?: string | null
  result?: string | null
  payload?: string | null
}

export type SessionHistoryResponse = {
  sessionId: string
  activeRunId: string | null
  activeRunStatus: RunStatus | null
  messages: SessionHistoryMessage[]
}

export type RunStatus = 'pending' | 'running' | 'completed' | 'failed'

export type StreamEvent = {
  runId: string
  sessionId: string
  sequence: number
  type: 'started' | 'delta' | 'completed' | 'failed' | 'tool_call' | 'tool_result'
  text: string | null
  data?: unknown
  timestamp: string
  status: RunStatus
}

export type ChatBubble = {
  id: string
  role: 'user' | 'assistant' | 'system' | 'tool'
  text: string
  isStreaming?: boolean
  runId?: string | null
  toolEventType?: 'tool_call' | 'tool_result'
  toolCallId?: string | null
  toolName?: string | null
  toolArguments?: string | null
  toolResult?: string | null
  toolExpanded?: boolean
  toolResultExpanded?: boolean
}

export type ToolCallEventData = {
  callId?: string | null
  toolName?: string | null
  arguments?: string | null
}

export type ToolResultEventData = {
  callId?: string | null
  result?: string | null
}
