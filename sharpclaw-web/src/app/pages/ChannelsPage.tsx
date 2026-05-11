import { useEffect, useState } from 'react'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import type { AgentConfig, Channel } from '../types/chat'
import { asErrorMessage } from '../utils/chatUtils'
import './AgentConsolePage.css'

export function ChannelsPage() {
  const [channels, setChannels] = useState<Channel[]>([])
  const [agents, setAgents] = useState<AgentConfig[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [showForm, setShowForm] = useState(false)
  const [editingChannel, setEditingChannel] = useState<Channel | null>(null)

  // Form state
  const [formName, setFormName] = useState('')
  const [formType, setFormType] = useState('discord')
  const [formAgentId, setFormAgentId] = useState<number>(1)
  const [formRoutingMode, setFormRoutingMode] = useState('shared')
  const [formConfig, setFormConfig] = useState('{}')
  const [formEnabled, setFormEnabled] = useState(true)

  useEffect(() => {
    void loadData()
  }, [])

  async function loadData() {
    setLoading(true)
    try {
      setError(null)
      const [channelsData, agentsData] = await Promise.all([
        fetchJson<{ channels: Channel[] }>(`${API_BASE_URL}/channels`),
        fetchJson<{ agents: AgentConfig[] }>(`${API_BASE_URL}/agents`),
      ])
      setChannels(channelsData.channels)
      setAgents(agentsData.agents)
      if (agentsData.agents.length > 0 && !formAgentId) {
        setFormAgentId(agentsData.agents[0].id)
      }
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  function openCreateForm() {
    setEditingChannel(null)
    setFormName('')
    setFormType('discord')
    setFormAgentId(agents[0]?.id ?? 1)
    setFormRoutingMode('shared')
    setFormConfig('{}')
    setFormEnabled(true)
    setShowForm(true)
  }

  function openEditForm(channel: Channel) {
    setEditingChannel(channel)
    setFormName(channel.name)
    setFormType(channel.type)
    setFormAgentId(channel.agentId)
    setFormRoutingMode(channel.routingMode)
    setFormConfig(channel.config)
    setFormEnabled(channel.enabled)
    setShowForm(true)
  }

  function closeForm() {
    setShowForm(false)
    setEditingChannel(null)
  }

  async function saveChannel() {
    if (!formName.trim()) {
      setError('Name is required.')
      return
    }

    // Validate config JSON
    try {
      JSON.parse(formConfig)
    } catch {
      setError('Config must be valid JSON.')
      return
    }

    try {
      setError(null)
      if (editingChannel) {
        await fetchJson(`${API_BASE_URL}/channels/${editingChannel.id}`, {
          method: 'PATCH',
          body: JSON.stringify({
            name: formName.trim(),
            type: formType,
            agentId: formAgentId,
            routingMode: formRoutingMode,
            config: formConfig,
            enabled: formEnabled,
          }),
        })
      } else {
        await fetchJson(`${API_BASE_URL}/channels`, {
          method: 'POST',
          body: JSON.stringify({
            name: formName.trim(),
            type: formType,
            agentId: formAgentId,
            routingMode: formRoutingMode,
            config: formConfig,
            enabled: formEnabled,
          }),
        })
      }
      closeForm()
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function toggleEnabled(channel: Channel) {
    try {
      setError(null)
      await fetchJson(`${API_BASE_URL}/channels/${channel.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ enabled: !channel.enabled }),
      })
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function deleteChannel(channel: Channel) {
    if (!window.confirm(`Delete channel "${channel.name}"?`)) return
    try {
      setError(null)
      await fetchJson(`${API_BASE_URL}/channels/${channel.id}`, { method: 'DELETE' })
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  return (
    <div className="manager-shell">
      <aside className="manager-sidebar">
        <div className="panel-header">
          <h1>Channels</h1>
          <p>Connect external platforms like Discord and Telegram.</p>
        </div>

        <div className="inline-actions" style={{ marginBottom: '12px' }}>
          <button type="button" className="button primary" onClick={openCreateForm}>
            Create Channel
          </button>
          <button type="button" className="button ghost" onClick={() => void loadData()}>
            Refresh
          </button>
        </div>

        {error && <div className="error-text" style={{ marginBottom: '8px' }}>{error}</div>}

        {loading && channels.length === 0 && <div className="empty-state">Loading...</div>}

        {!loading && channels.length === 0 && <div className="empty-state">No channels yet.</div>}

        {channels.map((channel) => (
          <div key={channel.id} className={`session-item ${!channel.enabled ? 'dimmed' : ''}`}>
            <div className="session-item-main">
              <span className="session-id">{channel.name}</span>
              <span className="session-meta">
                {channel.type} · {channel.routingMode} · Agent: {channel.agentId}
              </span>
              <span className="session-meta">
                {channel.enabled ? 'Enabled' : 'Disabled'}
              </span>
            </div>
            <div className="session-item-actions">
              <button type="button" className="button ghost" onClick={() => toggleEnabled(channel)}>
                {channel.enabled ? 'Disable' : 'Enable'}
              </button>
              <button type="button" className="button ghost" onClick={() => openEditForm(channel)}>
                Edit
              </button>
              <button type="button" className="button ghost" onClick={() => void deleteChannel(channel)}>
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
              <h2>Channel Details</h2>
              <p>Select a channel to edit, or create a new one.</p>
            </div>
          </div>
        ) : (
          <div className="agent-config" style={{ padding: '16px' }}>
            <h2 style={{ marginBottom: '12px' }}>
              {editingChannel ? `Edit: ${editingChannel.name}` : 'Create Channel'}
            </h2>

            <label className="field-label" htmlFor="channel-name">Name</label>
            <input
              id="channel-name"
              className="text-input"
              value={formName}
              onChange={(e) => setFormName(e.target.value)}
              placeholder="e.g. My Discord Bot"
            />

            <label className="field-label" htmlFor="channel-type">Type</label>
            <select
              id="channel-type"
              className="text-input"
              value={formType}
              onChange={(e) => setFormType(e.target.value)}
            >
              <option value="discord">Discord</option>
              <option value="telegram">Telegram</option>
            </select>

            <label className="field-label" htmlFor="channel-agent">Agent</label>
            <select
              id="channel-agent"
              className="text-input"
              value={formAgentId}
              onChange={(e) => setFormAgentId(Number(e.target.value))}
            >
              {agents.map((a) => (
                <option key={a.id} value={a.id}>{a.name} (ID: {a.id})</option>
              ))}
            </select>

            <label className="field-label" htmlFor="channel-routing">Routing Mode</label>
            <select
              id="channel-routing"
              className="text-input"
              value={formRoutingMode}
              onChange={(e) => setFormRoutingMode(e.target.value)}
            >
              <option value="shared">Shared — all users share one session</option>
              <option value="per_user">Per User — each user gets their own session</option>
            </select>

            <label className="field-label" htmlFor="channel-config">Config (JSON)</label>
            <textarea
              id="channel-config"
              className="composer-input"
              style={{ minHeight: '120px', fontFamily: 'monospace', fontSize: '13px' }}
              value={formConfig}
              onChange={(e) => setFormConfig(e.target.value)}
              placeholder='{"bot_token": "your-token-here"}'
            />
            <span className="session-meta" style={{ marginTop: '-8px', marginBottom: '8px', display: 'block' }}>
              Discord: {'{"bot_token": "..."}'} · Telegram: {'{"bot_token": "..."}'}
            </span>

            <label className="tool-toggle" style={{ marginTop: '8px' }}>
              <input
                type="checkbox"
                checked={formEnabled}
                onChange={(e) => setFormEnabled(e.target.checked)}
              />
              Enabled
            </label>

            <div className="inline-actions" style={{ marginTop: '12px' }}>
              <button type="button" className="button ghost" onClick={closeForm}>
                Cancel
              </button>
              <button type="button" className="button primary" onClick={() => void saveChannel()}>
                {editingChannel ? 'Save Changes' : 'Create Channel'}
              </button>
            </div>
          </div>
        )}
      </main>
    </div>
  )
}
