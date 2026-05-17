import { useEffect, useState } from 'react'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import type { AgentConfig, Secret } from '../types/chat'
import { asErrorMessage } from '../utils/chatUtils'
import './AgentConsolePage.css'

export function SecretsPage() {
  const [secrets, setSecrets] = useState<Secret[]>([])
  const [agents, setAgents] = useState<AgentConfig[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [showForm, setShowForm] = useState(false)
  const [editingSecret, setEditingSecret] = useState<Secret | null>(null)

  // Form state
  const [formName, setFormName] = useState('')
  const [formValue, setFormValue] = useState('')
  const [formScope, setFormScope] = useState('global')
  const [formOwnerId, setFormOwnerId] = useState<number | null>(null)
  const [formAllowBridge, setFormAllowBridge] = useState(false)

  useEffect(() => {
    void loadData()
  }, [])

  async function loadData() {
    setLoading(true)
    try {
      setError(null)
      const [secretsData, agentsData] = await Promise.all([
        fetchJson<{ secrets: Secret[] }>(`${API_BASE_URL}/secrets`),
        fetchJson<{ agents: AgentConfig[] }>(`${API_BASE_URL}/agents`),
      ])
      setSecrets(secretsData.secrets)
      setAgents(agentsData.agents)
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  function openCreateForm() {
    setEditingSecret(null)
    setFormName('')
    setFormValue('')
    setFormScope('global')
    setFormOwnerId(null)
    setFormAllowBridge(false)
    setShowForm(true)
  }

  function openEditForm(secret: Secret) {
    setEditingSecret(secret)
    setFormName(secret.name)
    setFormValue('')
    setFormScope(secret.scope)
    setFormOwnerId(secret.ownerId)
    setFormAllowBridge(secret.allowBridge)
    setShowForm(true)
  }

  function closeForm() {
    setShowForm(false)
    setEditingSecret(null)
  }

  async function saveSecret() {
    if (!formName.trim()) {
      setError('Name is required.')
      return
    }

    if (!editingSecret && !formValue.trim()) {
      setError('Value is required for new secrets.')
      return
    }

    try {
      setError(null)
      if (editingSecret) {
        const body: Record<string, unknown> = {
          scope: formScope,
          ownerId: formOwnerId,
          allowBridge: formAllowBridge,
        }
        if (formValue.trim()) {
          body.value = formValue.trim()
        }
        await fetchJson(`${API_BASE_URL}/secrets/${editingSecret.id}`, {
          method: 'PATCH',
          body: JSON.stringify(body),
        })
      } else {
        await fetchJson(`${API_BASE_URL}/secrets`, {
          method: 'POST',
          body: JSON.stringify({
            name: formName.trim(),
            value: formValue.trim(),
            scope: formScope,
            ownerId: formOwnerId,
            allowBridge: formAllowBridge,
          }),
        })
      }
      closeForm()
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function deleteSecret(secret: Secret) {
    if (!window.confirm(`Delete secret "${secret.name}"? This cannot be undone.`)) return
    try {
      setError(null)
      await fetchJson(`${API_BASE_URL}/secrets/${secret.id}`, { method: 'DELETE' })
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  return (
    <div className="manager-shell">
      <aside className="manager-sidebar">
        <div className="panel-header">
          <h1>Secrets</h1>
          <p>API keys, tokens, and credentials. Values are encrypted and never shown.</p>
        </div>

        <div className="inline-actions" style={{ marginBottom: '12px' }}>
          <button type="button" className="button primary" onClick={openCreateForm}>
            Add Secret
          </button>
          <button type="button" className="button ghost" onClick={() => void loadData()}>
            Refresh
          </button>
        </div>

        {error && <div className="error-text" style={{ marginBottom: '8px' }}>{error}</div>}

        {loading && secrets.length === 0 && <div className="empty-state">Loading...</div>}

        {!loading && secrets.length === 0 && <div className="empty-state">No secrets yet.</div>}

        {secrets.map((secret) => (
          <div key={secret.id} className="session-item">
            <div className="session-item-main">
              <span className="session-id">{secret.name}</span>
              <span className="session-meta">
                {secret.scope}{secret.ownerId ? ` (Agent: ${secret.ownerId})` : ''}
              </span>
              <span className="session-meta">
                {secret.allowBridge ? 'Bridge: yes' : 'Bridge: no'}
              </span>
            </div>
            <div className="session-item-actions">
              <button type="button" className="button ghost" onClick={() => openEditForm(secret)}>
                Edit
              </button>
              <button type="button" className="button ghost" onClick={() => void deleteSecret(secret)}>
                Delete
              </button>
            </div>
          </div>
        ))}
      </aside>

      <main className="manager-main">
        {!showForm ? (
          <div className="chat-header">
            <div>
              <h2>Secret Details</h2>
              <p>Select a secret to edit, or add a new one.</p>
            </div>
          </div>
        ) : (
          <div className="agent-config" style={{ padding: '16px' }}>
            <h2 style={{ marginBottom: '12px' }}>
              {editingSecret ? `Edit: ${editingSecret.name}` : 'Add Secret'}
            </h2>

            <label className="field-label" htmlFor="secret-name">Name</label>
            <input
              id="secret-name"
              className="text-input"
              value={formName}
              onChange={(e) => setFormName(e.target.value)}
              placeholder="e.g. github-token"
              disabled={!!editingSecret}
            />

            <label className="field-label" htmlFor="secret-value">
              {editingSecret ? 'New Value (leave blank to keep current)' : 'Value'}
            </label>
            <input
              id="secret-value"
              className="text-input"
              type="password"
              value={formValue}
              onChange={(e) => setFormValue(e.target.value)}
              placeholder={editingSecret ? 'Leave blank to keep current value' : 'Enter secret value'}
            />

            <label className="field-label" htmlFor="secret-scope">Scope</label>
            <select
              id="secret-scope"
              className="text-input"
              value={formScope}
              onChange={(e) => setFormScope(e.target.value)}
            >
              <option value="global">Global — available to all agents</option>
              <option value="agent">Agent — only available to the selected agent</option>
            </select>

            {formScope === 'agent' && (
              <>
                <label className="field-label" htmlFor="secret-owner">Agent</label>
                <select
                  id="secret-owner"
                  className="text-input"
                  value={formOwnerId ?? ''}
                  onChange={(e) => setFormOwnerId(e.target.value ? Number(e.target.value) : null)}
                >
                  <option value="">— Select agent —</option>
                  {agents.map((a) => (
                    <option key={a.id} value={a.id}>{a.name} (ID: {a.id})</option>
                  ))}
                </select>
              </>
            )}

            <label className="tool-toggle" style={{ marginTop: '8px' }}>
              <input
                type="checkbox"
                checked={formAllowBridge}
                onChange={(e) => setFormAllowBridge(e.target.checked)}
              />
              Allow bridge — send this secret to remote bridge clients
            </label>
            <span className="session-meta" style={{ marginTop: '2px', display: 'block' }}>
              Disable for highly sensitive secrets that should never leave this server.
            </span>

            <div className="inline-actions" style={{ marginTop: '12px' }}>
              <button type="button" className="button ghost" onClick={closeForm}>
                Cancel
              </button>
              <button type="button" className="button primary" onClick={() => void saveSecret()}>
                {editingSecret ? 'Save Changes' : 'Add Secret'}
              </button>
            </div>
          </div>
        )}
      </main>
    </div>
  )
}
