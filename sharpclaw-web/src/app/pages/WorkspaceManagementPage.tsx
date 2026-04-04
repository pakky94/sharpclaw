import { useEffect, useState } from 'react'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import type { AgentConfig, WorkspaceConfig, AgentWorkspaceEntry, ApprovalEvent } from '../types/chat'
import { asErrorMessage } from '../utils/chatUtils'
import './AgentConsolePage.css'

type WorkspaceManagementPageProps = {
  onUnsavedChange?: (hasUnsaved: boolean) => void
}

const POLICY_MODES = [
  { value: 'unrestricted', label: 'Unrestricted', description: 'All operations allowed; destructive commands still require approval' },
  { value: 'true_unrestricted', label: 'True Unrestricted', description: 'Everything allowed including destructive commands' },
  { value: 'confirm_writes_and_exec', label: 'Confirm Writes & Exec', description: 'Reads allowed; writes and commands require approval' },
  { value: 'confirm_exec_only', label: 'Confirm Exec Only', description: 'Reads and writes allowed; commands require approval' },
  { value: 'read_only', label: 'Read Only', description: 'Only read operations allowed' },
  { value: 'disabled', label: 'Disabled', description: 'No workspace tools available' },
]

type WorkspaceDraft = {
  name: string
  rootPath: string
  allowlistText: string
  denylistText: string
}

function emptyDraft(): WorkspaceDraft {
  return { name: '', rootPath: '', allowlistText: '', denylistText: '' }
}

export function WorkspaceManagementPage({ onUnsavedChange }: WorkspaceManagementPageProps) {
  const [agents, setAgents] = useState<AgentConfig[]>([])
  const [selectedAgentId, setSelectedAgentId] = useState<number | null>(null)
  const [agentWorkspaces, setAgentWorkspaces] = useState<AgentWorkspaceEntry[]>([])
  const [workspaces, setWorkspaces] = useState<WorkspaceConfig[]>([])
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<number | null>(null)
  const [draft, setDraft] = useState<WorkspaceDraft>(emptyDraft())
  const [dirty, setDirty] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [approvals, setApprovals] = useState<ApprovalEvent[]>([])
  const [approvalsSessionId, setApprovalsSessionId] = useState('')
  const [approvalsLoading, setApprovalsLoading] = useState(false)

  useEffect(() => {
    void loadAgents()
    void loadWorkspaces()
  }, [])

  useEffect(() => {
    onUnsavedChange?.(dirty)
  }, [dirty, onUnsavedChange])

  useEffect(() => {
    if (selectedAgentId === null) {
      setAgentWorkspaces([])
      return
    }
    void loadAgentWorkspaces(selectedAgentId)
  }, [selectedAgentId])

  function applyWorkspace(ws: WorkspaceConfig) {
    setDraft({
      name: ws.name,
      rootPath: ws.rootPath,
      allowlistText: ws.allowlistPatterns.join('\n'),
      denylistText: ws.denylistPatterns.join('\n'),
    })
    setDirty(false)
  }

  function resetForm() {
    setDraft(emptyDraft())
    setDirty(false)
    setSelectedWorkspaceId(null)
  }

  async function loadAgents(preferredAgentId?: number) {
    try {
      setError(null)
      const data = await fetchJson<{ agents: AgentConfig[] }>(`${API_BASE_URL}/agents`)
      setAgents(data.agents)
      if (data.agents.length > 0) {
        const preferred =
          preferredAgentId !== undefined
            ? data.agents.find((a) => a.id === preferredAgentId)
            : data.agents.find((a) => a.id === selectedAgentId)
        setSelectedAgentId((preferred ?? data.agents[0]).id)
      }
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function loadWorkspaces() {
    try {
      const data = await fetchJson<{ workspaces: WorkspaceConfig[] }>(`${API_BASE_URL}/workspaces`)
      setWorkspaces(data.workspaces)
    } catch {
      setWorkspaces([])
    }
  }

  async function loadAgentWorkspaces(agentId: number) {
    try {
      const data = await fetchJson<{ workspaces: AgentWorkspaceEntry[] }>(`${API_BASE_URL}/agents/${agentId}/workspaces`)
      setAgentWorkspaces(data.workspaces)
    } catch {
      setAgentWorkspaces([])
    }
  }

  async function saveWorkspace() {
    if (!draft.name.trim()) {
      setError('Workspace name is required.')
      return
    }

    try {
      setError(null)
      setLoading(true)
      const result = await fetchJson<WorkspaceConfig>(`${API_BASE_URL}/workspaces`, {
        method: 'PUT',
        body: JSON.stringify({
          name: draft.name.trim(),
          rootPath: draft.rootPath.trim() || undefined,
          allowlistPatterns: draft.allowlistText.split('\n').map((l) => l.trim()).filter(Boolean),
          denylistPatterns: draft.denylistText.split('\n').map((l) => l.trim()).filter(Boolean),
        }),
      })
      await loadWorkspaces()
      setSelectedWorkspaceId(result.id)
      setDirty(false)
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  async function deleteWorkspace() {
    if (selectedWorkspaceId === null) return
    try {
      setError(null)
      await fetchJson(`${API_BASE_URL}/workspaces/${selectedWorkspaceId}`, { method: 'DELETE' })
      await loadWorkspaces()
      resetForm()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function assignWorkspace(workspaceId: number, policyMode: string, isDefault: boolean) {
    if (selectedAgentId === null) return
    try {
      setError(null)
      await fetchJson(`${API_BASE_URL}/agents/${selectedAgentId}/workspaces/${workspaceId}`, {
        method: 'PUT',
        body: JSON.stringify({ policyMode, isDefault }),
      })
      await loadAgentWorkspaces(selectedAgentId)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function unassignWorkspace(workspaceId: number) {
    if (selectedAgentId === null) return
    try {
      setError(null)
      await fetchJson(`${API_BASE_URL}/agents/${selectedAgentId}/workspaces/${workspaceId}`, { method: 'DELETE' })
      await loadAgentWorkspaces(selectedAgentId)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function loadApprovals() {
    if (!approvalsSessionId.trim()) return
    try {
      setError(null)
      setApprovalsLoading(true)
      const data = await fetchJson<{ sessionId: string; approvals: ApprovalEvent[] }>(
        `${API_BASE_URL}/sessions/${approvalsSessionId.trim()}/approvals/pending`,
      )
      setApprovals(data.approvals)
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setApprovalsLoading(false)
    }
  }

  async function resolveApproval(token: string, approved: boolean) {
    try {
      setError(null)
      const method = approved ? 'approve' : 'reject'
      await fetchJson(
        `${API_BASE_URL}/sessions/${approvalsSessionId.trim()}/approvals/${token}/${method}`,
        { method: 'POST' },
      )
      await loadApprovals()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  function selectWorkspaceForEdit(ws: WorkspaceConfig) {
    setSelectedWorkspaceId(ws.id)
    applyWorkspace(ws)
  }

  const selectedWorkspace = workspaces.find((w) => w.id === selectedWorkspaceId) ?? null

  return (
    <div className="manager-shell">
      <aside className="manager-sidebar">
        <div className="panel-header">
          <h1>Workspaces</h1>
          <p>Create workspaces and assign them to agents.</p>
        </div>

        <label className="field-label" htmlFor="agent-select-workspace">
          Agent
        </label>
        <select
          id="agent-select-workspace"
          className="text-input"
          value={selectedAgentId ?? ''}
          onChange={(event) => setSelectedAgentId(Number(event.target.value))}
        >
          {agents.map((agent) => (
            <option key={agent.id} value={agent.id}>
              {agent.id} - {agent.name}
            </option>
          ))}
        </select>

        {selectedAgentId !== null && (
          <>
            <div className="files-header">
              <h3>Assigned Workspaces</h3>
            </div>
            <div className="files-list">
              {agentWorkspaces.length === 0 && <div className="empty-state">No workspaces assigned.</div>}
              {agentWorkspaces.map((entry) => {
                const policyLabel = POLICY_MODES.find((m) => m.value === entry.assignment.policyMode)?.label ?? entry.assignment.policyMode
                return (
                  <div
                    key={entry.workspace.id}
                    className={`session-card ${selectedWorkspaceId === entry.workspace.id ? 'session-card-selected' : ''}`}
                    style={{ cursor: 'pointer' }}
                    onClick={() => selectWorkspaceForEdit(entry.workspace)}
                  >
                    <div className="session-id">
                      {entry.workspace.name}
                      {entry.assignment.isDefault ? ' (default)' : ''}
                    </div>
                    <div className="session-meta">
                      <span>{policyLabel}</span>
                      <button
                        type="button"
                        className="button ghost"
                        style={{ padding: '2px 6px', fontSize: '0.7rem' }}
                        onClick={(e) => {
                          e.stopPropagation()
                          void unassignWorkspace(entry.workspace.id)
                        }}
                      >
                        Remove
                      </button>
                    </div>
                  </div>
                )
              })}
            </div>

            {workspaces.length > 0 && (
              <div className="workspace-assign-section">
                <label className="field-label">Assign Workspace</label>
                {workspaces
                  .filter((w) => !agentWorkspaces.some((a) => a.workspace.id === w.id))
                  .map((ws) => (
                    <div key={ws.id} className="inline-actions" style={{ marginBottom: '4px' }}>
                      <select
                        className="text-input"
                        style={{ fontSize: '0.75rem', flex: 1 }}
                        id={`policy-${ws.id}`}
                        defaultValue="confirm_writes_and_exec"
                      >
                        {POLICY_MODES.map((m) => (
                          <option key={m.value} value={m.value}>{m.label}</option>
                        ))}
                      </select>
                      <button
                        type="button"
                        className="button ghost"
                        style={{ fontSize: '0.75rem' }}
                        onClick={() => {
                          const sel = document.getElementById(`policy-${ws.id}`) as HTMLSelectElement
                          void assignWorkspace(ws.id, sel.value, agentWorkspaces.length === 0)
                        }}
                      >
                        + {ws.name}
                      </button>
                    </div>
                  ))}
              </div>
            )}
          </>
        )}

        <div className="workspace-approvals-section">
          <h3>Approvals</h3>
          <div className="inline-actions">
            <input
              className="text-input"
              placeholder="Session ID"
              value={approvalsSessionId}
              onChange={(e) => setApprovalsSessionId(e.target.value)}
              style={{ fontSize: '0.8rem' }}
            />
            <button
              type="button"
              className="button ghost"
              onClick={() => void loadApprovals()}
              disabled={!approvalsSessionId.trim() || approvalsLoading}
            >
              Load
            </button>
          </div>
          <div className="approvals-list">
            {approvals.length === 0 && <div className="empty-state">No pending approvals.</div>}
            {approvals.map((approval) => (
              <div key={approval.approvalToken} className="approval-card">
                <div className="approval-header">
                  <span className={`approval-risk ${approval.riskLevel}`}>{approval.riskLevel}</span>
                  <span className="approval-action">{approval.actionType}</span>
                </div>
                {approval.targetPath && (
                  <div className="approval-detail">
                    <span>Path:</span>
                    <code>{approval.targetPath}</code>
                  </div>
                )}
                {approval.commandPreview && (
                  <div className="approval-detail">
                    <span>Command:</span>
                    <code>{approval.commandPreview}</code>
                  </div>
                )}
                <div className="approval-actions">
                  <button
                    type="button"
                    className="button primary"
                    onClick={() => void resolveApproval(approval.approvalToken, true)}
                  >
                    Approve
                  </button>
                  <button
                    type="button"
                    className="button ghost"
                    onClick={() => void resolveApproval(approval.approvalToken, false)}
                  >
                    Reject
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      </aside>

      <main className="manager-main">
        <div className="chat-header">
          <div>
            <h2>{selectedWorkspace ? 'Edit Workspace' : 'New Workspace'}</h2>
            <p>
              {selectedWorkspace
                ? `Editing "${selectedWorkspace.name}".`
                : 'Create a new workspace to assign to agents.'}
            </p>
          </div>
        </div>

        <label className="field-label" htmlFor="workspace-name">
          Name
        </label>
        <input
          id="workspace-name"
          className="text-input"
          placeholder="e.g. api-service, web-frontend"
          value={draft.name}
          onChange={(e) => {
            setDraft((d) => ({ ...d, name: e.target.value }))
            setDirty(true)
          }}
          disabled={loading}
        />

        <label className="field-label" htmlFor="workspace-root-path">
          Root Path
        </label>
        <input
          id="workspace-root-path"
          className="text-input"
          placeholder="Leave empty for default"
          value={draft.rootPath}
          onChange={(e) => {
            setDraft((d) => ({ ...d, rootPath: e.target.value }))
            setDirty(true)
          }}
          disabled={loading}
        />

        <label className="field-label" htmlFor="workspace-allowlist">
          Allowlist Patterns (one per line, empty = all allowed)
        </label>
        <textarea
          id="workspace-allowlist"
          className="text-input pattern-list"
          placeholder="*.ts&#10;src/**&#10;*.md"
          value={draft.allowlistText}
          onChange={(e) => {
            setDraft((d) => ({ ...d, allowlistText: e.target.value }))
            setDirty(true)
          }}
          disabled={loading}
        />

        <label className="field-label" htmlFor="workspace-denylist">
          Denylist Patterns (one per line)
        </label>
        <textarea
          id="workspace-denylist"
          className="text-input pattern-list"
          placeholder=".git/**&#10;node_modules/**&#10;*.lock"
          value={draft.denylistText}
          onChange={(e) => {
            setDraft((d) => ({ ...d, denylistText: e.target.value }))
            setDirty(true)
          }}
          disabled={loading}
        />

        <div className="composer-footer">
          {error && <span className="error-text">{error}</span>}
          <div className="inline-actions">
            <button
              type="button"
              className="button ghost"
              onClick={() => {
                if (selectedWorkspace) {
                  applyWorkspace(selectedWorkspace)
                } else {
                  resetForm()
                }
              }}
              disabled={loading || !dirty}
            >
              Reset
            </button>
            {selectedWorkspace && (
              <button
                type="button"
                className="button ghost"
                onClick={() => void deleteWorkspace()}
                disabled={loading}
              >
                Delete
              </button>
            )}
            <button
              type="button"
              className="button primary"
              onClick={() => void saveWorkspace()}
              disabled={loading || !draft.name.trim() || !dirty}
            >
              {selectedWorkspace ? 'Update' : 'Create'}
            </button>
          </div>
        </div>
      </main>
    </div>
  )
}
