import { useState } from 'react'
import type { SessionSummary, WorkspaceConfig } from '../types/chat'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import { asErrorMessage } from '../utils/chatUtils'

type ChatHeaderProps = {
  selectedSession: SessionSummary | null
  hasUnsavedDraft: boolean
  showToolEvents: boolean
  apiBaseUrl: string
  onShowToolEventsChange: (nextValue: boolean) => void
  onError?: (message: string) => void
}

type ActiveWorkspace = {
  name: string
  policyMode: string
}

export function ChatHeader({ selectedSession, hasUnsavedDraft, showToolEvents, apiBaseUrl, onShowToolEventsChange, onError }: ChatHeaderProps) {
  const [activeWorkspaces, setActiveWorkspaces] = useState<ActiveWorkspace[]>([])
  const [availableWorkspaces, setAvailableWorkspaces] = useState<WorkspaceConfig[]>([])
  const [showWsDropdown, setShowWsDropdown] = useState(false)

  async function loadWorkspaces() {
    if (!selectedSession) return
    try {
      const [activeRes, availableRes] = await Promise.all([
        fetchJson<{ activeWorkspaces: { name: string; policyMode: string }[] }>(
          `${API_BASE_URL}/sessions/${selectedSession.sessionId}/active-workspaces`,
        ),
        fetchJson<{ availableWorkspaces: { workspace: WorkspaceConfig; policyMode: string; isDefault: boolean }[] }>(
          `${API_BASE_URL}/agents/${selectedSession.agentId}/available-workspaces`,
        ),
      ])
      setActiveWorkspaces(activeRes.activeWorkspaces)
      setAvailableWorkspaces(availableRes.availableWorkspaces.map((w) => w.workspace))
      setShowWsDropdown(true)
    } catch (e) {
      onError?.(asErrorMessage(e))
    }
  }

  async function toggleWorkspace(name: string) {
    if (!selectedSession) return
    const isActive = activeWorkspaces.some((w) => w.name === name)
    const next = isActive
      ? activeWorkspaces.filter((w) => w.name !== name)
      : [...activeWorkspaces, { name, policyMode: '' }]

    try {
      const res = await fetchJson<{ activeWorkspaces: { name: string; policyMode: string }[] }>(
        `${API_BASE_URL}/sessions/${selectedSession.sessionId}/active-workspaces`,
        {
          method: 'PUT',
          body: JSON.stringify({ workspaceNames: next.map((w) => w.name) }),
        },
      )
      setActiveWorkspaces(res.activeWorkspaces)
    } catch (e) {
      onError?.(asErrorMessage(e))
    }
  }

  return (
    <header className="chat-header">
      <div>
        <h2>{selectedSession ? `Session ${selectedSession.sessionId.slice(0, 8)}` : 'No Session Selected'}{hasUnsavedDraft ? ' *' : ''}</h2>
        <p>{selectedSession ? `Agent ${selectedSession.agentId}` : 'Create a session to start chatting.'}</p>
        {activeWorkspaces.length > 0 && (
          <div className="active-workspaces-bar">
            {activeWorkspaces.map((ws) => (
              <span key={ws.name} className="active-workspace-tag">{ws.name}</span>
            ))}
          </div>
        )}
      </div>
      <div className="chat-controls">
        {selectedSession && (
          <div className="workspace-dropdown-wrapper">
            <button
              type="button"
              className="button ghost workspace-dropdown-btn"
              onClick={() => {
                if (!showWsDropdown) void loadWorkspaces()
                else setShowWsDropdown(false)
              }}
            >
              Workspaces ({activeWorkspaces.length})
            </button>
            {showWsDropdown && (
              <div className="workspace-dropdown">
                {availableWorkspaces.length === 0 && <div className="empty-state">No workspaces available.</div>}
                {availableWorkspaces.map((ws) => {
                  const isActive = activeWorkspaces.some((w) => w.name === ws.name)
                  return (
                    <label key={ws.id} className="workspace-dropdown-item">
                      <input
                        type="checkbox"
                        checked={isActive}
                        onChange={() => void toggleWorkspace(ws.name)}
                      />
                      <span>{ws.name}</span>
                    </label>
                  )
                })}
              </div>
            )}
          </div>
        )}
        <label className="tool-toggle">
          <input type="checkbox" checked={showToolEvents} onChange={(event) => onShowToolEventsChange(event.target.checked)} />
          Show tool calls/results
        </label>
        <code>{apiBaseUrl}</code>
      </div>
    </header>
  )
}
