import { useEffect, useState } from 'react'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import type { AgentConfig, ScheduledJob } from '../types/chat'
import { asErrorMessage } from '../utils/chatUtils'
import './AgentConsolePage.css'

export function ScheduledJobsPage() {
  const [jobs, setJobs] = useState<ScheduledJob[]>([])
  const [agents, setAgents] = useState<AgentConfig[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [showForm, setShowForm] = useState(false)
  const [editingJob, setEditingJob] = useState<ScheduledJob | null>(null)

  // Form state
  const [formName, setFormName] = useState('')
  const [formCron, setFormCron] = useState('')
  const [formTimezone, setFormTimezone] = useState('Europe/Rome')
  const [formPrompt, setFormPrompt] = useState('')
  const [formAgentId, setFormAgentId] = useState<number>(1)
  const [formEnabled, setFormEnabled] = useState(true)

  useEffect(() => {
    void loadData()
  }, [])

  async function loadData() {
    setLoading(true)
    try {
      setError(null)
      const [jobsData, agentsData] = await Promise.all([
        fetchJson<{ jobs: ScheduledJob[] }>(`${API_BASE_URL}/jobs`),
        fetchJson<{ agents: AgentConfig[] }>(`${API_BASE_URL}/agents`),
      ])
      setJobs(jobsData.jobs)
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
    setEditingJob(null)
    setFormName('')
    setFormCron('')
    setFormTimezone('Europe/Rome')
    setFormPrompt('')
    setFormAgentId(agents[0]?.id ?? 1)
    setFormEnabled(true)
    setShowForm(true)
  }

  function openEditForm(job: ScheduledJob) {
    setEditingJob(job)
    setFormName(job.name)
    setFormCron(job.cronExpression)
    setFormTimezone(job.timezone)
    setFormPrompt(job.prompt)
    setFormAgentId(job.agentId)
    setFormEnabled(job.enabled)
    setShowForm(true)
  }

  function closeForm() {
    setShowForm(false)
    setEditingJob(null)
  }

  async function saveJob() {
    if (!formName.trim() || !formCron.trim() || !formPrompt.trim()) {
      setError('Name, cron expression, and prompt are required.')
      return
    }

    try {
      setError(null)
      if (editingJob) {
        await fetchJson(`${API_BASE_URL}/jobs/${editingJob.id}`, {
          method: 'PATCH',
          body: JSON.stringify({
            name: formName.trim(),
            cronExpression: formCron.trim(),
            timezone: formTimezone,
            prompt: formPrompt.trim(),
            agentId: formAgentId,
            enabled: formEnabled,
          }),
        })
      } else {
        await fetchJson(`${API_BASE_URL}/jobs`, {
          method: 'POST',
          body: JSON.stringify({
            name: formName.trim(),
            cronExpression: formCron.trim(),
            timezone: formTimezone,
            prompt: formPrompt.trim(),
            agentId: formAgentId,
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

  async function toggleEnabled(job: ScheduledJob) {
    try {
      setError(null)
      await fetchJson(`${API_BASE_URL}/jobs/${job.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ enabled: !job.enabled }),
      })
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function deleteJob(job: ScheduledJob) {
    if (!window.confirm(`Delete job "${job.name}"?`)) return
    try {
      setError(null)
      await fetchJson(`${API_BASE_URL}/jobs/${job.id}`, { method: 'DELETE' })
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  function formatDate(iso: string | null): string {
    if (!iso) return '—'
    return new Date(iso).toLocaleString()
  }

  return (
    <div className="manager-shell">
      <aside className="manager-sidebar">
        <div className="panel-header">
          <h1>Scheduled Jobs</h1>
          <p>Create and manage cron-based agent jobs.</p>
        </div>

        <div className="inline-actions" style={{ marginBottom: '12px' }}>
          <button type="button" className="button primary" onClick={openCreateForm}>
            Create Job
          </button>
          <button type="button" className="button ghost" onClick={() => void loadData()}>
            Refresh
          </button>
        </div>

        {error && <div className="error-text" style={{ marginBottom: '8px' }}>{error}</div>}

        {loading && jobs.length === 0 && <div className="empty-state">Loading...</div>}

        {!loading && jobs.length === 0 && <div className="empty-state">No scheduled jobs yet.</div>}

        {jobs.map((job) => (
          <div key={job.id} className={`session-item ${!job.enabled ? 'dimmed' : ''}`}>
            <div className="session-item-main">
              <span className="session-id">{job.name}</span>
              <span className="session-meta">
                {job.cronExpression} · {job.timezone}
              </span>
              <span className="session-meta">
                Agent: {job.agentId} · {job.enabled ? 'Enabled' : 'Disabled'}
              </span>
              <span className="session-meta">
                Last run: {formatDate(job.lastRunAt)} · Next: {formatDate(job.nextRunAt)}
              </span>
            </div>
            <div className="session-item-actions">
              <button type="button" className="button ghost" onClick={() => toggleEnabled(job)}>
                {job.enabled ? 'Disable' : 'Enable'}
              </button>
              <button type="button" className="button ghost" onClick={() => openEditForm(job)}>
                Edit
              </button>
              <button type="button" className="button ghost" onClick={() => void deleteJob(job)}>
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
              <h2>Job Details</h2>
              <p>Select a job to edit, or create a new one.</p>
            </div>
          </div>
        ) : (
          <div className="agent-config" style={{ padding: '16px' }}>
            <h2 style={{ marginBottom: '12px' }}>
              {editingJob ? `Edit: ${editingJob.name}` : 'Create Job'}
            </h2>

            <label className="field-label" htmlFor="job-name">Name</label>
            <input
              id="job-name"
              className="text-input"
              value={formName}
              onChange={(e) => setFormName(e.target.value)}
              placeholder="e.g. Morning briefing"
            />

            <label className="field-label" htmlFor="job-cron">Cron Expression</label>
            <input
              id="job-cron"
              className="text-input"
              value={formCron}
              onChange={(e) => setFormCron(e.target.value)}
              placeholder="e.g. 0 8 * * *"
            />
            <span className="session-meta" style={{ marginTop: '-8px', marginBottom: '8px', display: 'block' }}>
              Format: minute hour day month weekday (e.g. 30 9 * * 1-5 = 9:30 AM weekdays)
            </span>

            <label className="field-label" htmlFor="job-timezone">Timezone</label>
            <input
              id="job-timezone"
              className="text-input"
              value={formTimezone}
              onChange={(e) => setFormTimezone(e.target.value)}
              placeholder="Europe/Rome"
            />

            <label className="field-label" htmlFor="job-agent">Agent</label>
            <select
              id="job-agent"
              className="text-input"
              value={formAgentId}
              onChange={(e) => setFormAgentId(Number(e.target.value))}
            >
              {agents.map((a) => (
                <option key={a.id} value={a.id}>{a.name} (ID: {a.id})</option>
              ))}
            </select>

            <label className="field-label" htmlFor="job-prompt">Prompt</label>
            <textarea
              id="job-prompt"
              className="composer-input"
              style={{ minHeight: '120px' }}
              value={formPrompt}
              onChange={(e) => setFormPrompt(e.target.value)}
              placeholder="The prompt that will be sent to the agent when this job fires..."
            />

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
              <button type="button" className="button primary" onClick={() => void saveJob()}>
                {editingJob ? 'Save Changes' : 'Create Job'}
              </button>
            </div>
          </div>
        )}
      </main>
    </div>
  )
}
