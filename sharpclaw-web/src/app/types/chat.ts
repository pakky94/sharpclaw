export type SessionSummary = {
  sessionId: string
  agentId: number
  name: string | null
  visibleInSidebar: boolean
  parentSessionId: string | null
  createdAt: string
  updatedAt: string
  messagesCount: number
}

export type AgentConfig = {
  id: number
  name: string
  llmModel: string
  temperature: number
  createdAt: string
  updatedAt: string
}

export type AgentFileSummary = {
  name: string
}

export type AgentFragmentSummary = {
  name: string
  path: string
  hasChildren: boolean
}

export type AgentFile = {
  path: string
  content: string
}

export type SessionHistoryMessage = {
  role: string
  text: string | null
  contents: SessionMessageContent[]
  authorName: string | null
  messageId: number
  runStatus: RunStatus | null
}

export type SessionMessageContent = {
  type: string
  text?: string | null
  callId?: string | null
  toolName?: string | null
  arguments?: unknown
  result?: unknown
  payload?: unknown
}

export type SessionHistoryResponse = {
  sessionId: string
  parentSessionId: string | null
  runStatus: RunStatus | null
  latestSequenceId: number
  messages: SessionHistoryMessage[]
  childSessions: SessionChildLink[]
}

export type SessionChildLink = {
  callId: string
  childSessionId: string
  completed: boolean
}

export type RunStatus = 'pending' | 'waiting' | 'running' | 'completed' | 'failed'

export type StreamEvent = {
  messageId: number
  sessionId: string
  sequence: number
  type: 'started' | 'delta' | 'completed' | 'failed' | 'tool_call' | 'tool_result' | 'approval_required' | 'child_session_spawned'
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
  messageId: number
  toolEventType?: 'tool_call' | 'tool_result'
  toolCallId?: string | null
  toolName?: string | null
  toolArguments?: string | null
  toolResult?: string | null
  toolResultFormat?: 'text' | 'structured'
  toolExpanded?: boolean
  toolResultExpanded?: boolean
  childSessionId?: string | null
  childSessionCompleted?: boolean
}

export type ToolCallEventData = {
  callId?: string | null
  toolName?: string | null
  arguments?: unknown
}

export type ToolResultEventData = {
  callId?: string | null
  result?: unknown
}

export type ChildSessionSpawnedEventData = {
  callId?: string | null
  childSessionId?: string | null
  description?: string | null
}

export type WorkspaceConfig = {
  id: number
  name: string
  rootPath: string
  allowlistPatterns: string[]
  denylistPatterns: string[]
  createdAt: string
  updatedAt: string
}

export type WorkspaceAssignment = {
  id: number
  policyMode: string
  isDefault: boolean
}

export type AgentWorkspaceEntry = {
  workspace: WorkspaceConfig
  assignment: WorkspaceAssignment
}

export type ApprovalEvent = {
  id: number
  sessionId: string
  agentId: number
  approvalToken: string
  actionType: string
  targetPath: string | null
  commandPreview: string | null
  riskLevel: string
  status: string
  createdAt: string
  resolvedAt: string | null
}
