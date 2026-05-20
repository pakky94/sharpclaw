import { useEffect, useMemo, useState } from 'react'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import type { BackupArtifact, BackupConfig, BackupRun } from '../types/chat'
import { asErrorMessage } from '../utils/chatUtils'
import './AgentConsolePage.css'

type BackupRunsResponse = { runs: BackupRun[] }
type BackupArtifactsResponse = { artifacts: BackupArtifact[] }
type SelectedItem = { kind: 'run'; run: BackupRun } | { kind: 'artifact'; artifact: BackupArtifact }

export function BackupPage() {
  const [config, setConfig] = useState<BackupConfig | null>(null)
  const [runs, setRuns] = useState<BackupRun[]>([])
  const [artifacts, setArtifacts] = useState<BackupArtifact[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [savingConfig, setSavingConfig] = useState(false)
  const [runningMode, setRunningMode] = useState<'full' | 'incremental' | null>(null)
  const [restoringId, setRestoringId] = useState<string | null>(null)
  const [deletingRunId, setDeletingRunId] = useState<string | null>(null)
  const [deleteArtifactWithRun, setDeleteArtifactWithRun] = useState(true)

  const [formEnabled, setFormEnabled] = useState(true)
  const [formTimezone, setFormTimezone] = useState('Europe/Rome')
  const [formDailyTime, setFormDailyTime] = useState('03:00:00')
  const [formFullEveryN, setFormFullEveryN] = useState('7')
  const [formRetentionDays, setFormRetentionDays] = useState('')
  const [formRetentionFullChains, setFormRetentionFullChains] = useState('')
  const [formStrictRestoreDefault, setFormStrictRestoreDefault] = useState(true)
  const [formStorageRoot, setFormStorageRoot] = useState('/data/backups')

  useEffect(() => {
    void loadData()
  }, [])

  const selectedItem = useMemo<SelectedItem | null>(() => {
    if (!selectedId) return null
    const run = runs.find((r) => r.backupId === selectedId)
    if (run) return { kind: 'run', run }
    const artifact = artifacts.find((a) => a.backupId === selectedId)
    if (artifact) return { kind: 'artifact', artifact }
    return null
  }, [selectedId, runs, artifacts])

  async function loadData() {
    setLoading(true)
    try {
      setError(null)
      const [configData, runsData, artifactsData] = await Promise.all([
        fetchJson<BackupConfig>(`${API_BASE_URL}/backups/config`),
        fetchJson<BackupRunsResponse>(`${API_BASE_URL}/backups/runs?limit=200`),
        fetchJson<BackupArtifactsResponse>(`${API_BASE_URL}/backups/artifacts?limit=500`),
      ])

      setConfig(configData)
      setRuns(runsData.runs)
      setArtifacts(artifactsData.artifacts)

      setFormEnabled(configData.enabled)
      setFormTimezone(configData.timezone)
      setFormDailyTime(configData.dailyTime)
      setFormFullEveryN(String(configData.fullEveryN))
      setFormRetentionDays(configData.retentionDays === null ? '' : String(configData.retentionDays))
      setFormRetentionFullChains(configData.retentionFullChains === null ? '' : String(configData.retentionFullChains))
      setFormStrictRestoreDefault(configData.strictRestoreDefault)
      setFormStorageRoot(configData.storageRoot)

      const allIds = new Set<string>([
        ...runsData.runs.map((r) => r.backupId),
        ...artifactsData.artifacts.map((a) => a.backupId),
      ])
      if (!selectedId) {
        setSelectedId(runsData.runs[0]?.backupId ?? artifactsData.artifacts[0]?.backupId ?? null)
      } else if (!allIds.has(selectedId)) {
        setSelectedId(runsData.runs[0]?.backupId ?? artifactsData.artifacts[0]?.backupId ?? null)
      }
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  function normalizeTimeInput(value: string): string {
    if (/^\d{2}:\d{2}$/.test(value)) return `${value}:00`
    return value
  }

  function parseOptionalInt(value: string): number | null {
    const trimmed = value.trim()
    if (trimmed.length === 0) return null
    const parsed = Number(trimmed)
    if (!Number.isInteger(parsed)) throw new Error('Optional numeric fields must be whole numbers.')
    return parsed
  }

  async function saveConfig() {
    if (!config) return

    let fullEveryN: number
    let retentionDays: number | null
    let retentionFullChains: number | null
    try {
      fullEveryN = Number(formFullEveryN.trim())
      if (!Number.isInteger(fullEveryN) || fullEveryN < 1) {
        setError('Full cadence must be an integer greater than or equal to 1.')
        return
      }

      retentionDays = parseOptionalInt(formRetentionDays)
      retentionFullChains = parseOptionalInt(formRetentionFullChains)
      if (retentionDays !== null && retentionDays < 1) {
        setError('Retention days must be empty or at least 1.')
        return
      }
      if (retentionFullChains !== null && retentionFullChains < 1) {
        setError('Retention full chains must be empty or at least 1.')
        return
      }
    } catch (e) {
      setError(asErrorMessage(e))
      return
    }

    try {
      setSavingConfig(true)
      setError(null)
      const updated = await fetchJson<BackupConfig>(`${API_BASE_URL}/backups/config`, {
        method: 'PUT',
        body: JSON.stringify({
          enabled: formEnabled,
          timezone: formTimezone.trim(),
          dailyTime: normalizeTimeInput(formDailyTime.trim()),
          fullEveryN,
          retentionDays,
          retentionFullChains,
          strictRestoreDefault: formStrictRestoreDefault,
          storageRoot: formStorageRoot.trim(),
        }),
      })
      setConfig(updated)
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setSavingConfig(false)
    }
  }

  async function runBackup(mode: 'full' | 'incremental') {
    try {
      setRunningMode(mode)
      setError(null)
      const run = await fetchJson<BackupRun>(`${API_BASE_URL}/backups/run`, {
        method: 'POST',
        body: JSON.stringify({ mode }),
      })
      setSelectedId(run.backupId)
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setRunningMode(null)
    }
  }

  async function restoreSelected(strict: boolean) {
    if (!selectedItem) return

    const backupId = selectedItem.kind === 'run' ? selectedItem.run.backupId : selectedItem.artifact.backupId
    const confirmation = window.confirm(
      `Restore backup ${backupId}?\nThis will overwrite current data.`
    )
    if (!confirmation) return

    try {
      setRestoringId(backupId)
      setError(null)
      await fetchJson<{ restored: boolean }>(`${API_BASE_URL}/backups/restore`, {
        method: 'POST',
        body: JSON.stringify(
          selectedItem.kind === 'run'
            ? { backupId, strict }
            : { artifactPath: selectedItem.artifact.artifactPath, strict }
        ),
      })
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setRestoringId(null)
    }
  }

  async function deleteSelectedRun() {
    if (!selectedItem || selectedItem.kind !== 'run') return
    const run = selectedItem.run
    const confirmation = window.confirm(`Delete backup run ${run.backupId}?`)
    if (!confirmation) return

    try {
      setDeletingRunId(run.backupId)
      setError(null)
      await fetchJson<{ deleted: boolean }>(
        `${API_BASE_URL}/backups/runs/${run.backupId}?deleteArtifact=${deleteArtifactWithRun ? 'true' : 'false'}`,
        { method: 'DELETE' },
      )
      await loadData()
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setDeletingRunId(null)
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
          <h1>Backups</h1>
          <p>Configure scheduling, run backups, restore, and clean up runs.</p>
        </div>

        <div className="inline-actions" style={{ marginBottom: '12px' }}>
          <button type="button" className="button ghost" onClick={() => void loadData()} disabled={loading}>
            Refresh
          </button>
          <button type="button" className="button primary" onClick={() => void runBackup('full')} disabled={!!runningMode || loading}>
            {runningMode === 'full' ? 'Running Full...' : 'Run Full'}
          </button>
          <button type="button" className="button primary" onClick={() => void runBackup('incremental')} disabled={!!runningMode || loading}>
            {runningMode === 'incremental' ? 'Running Incremental...' : 'Run Incremental'}
          </button>
        </div>

        {error && <div className="error-text" style={{ marginBottom: '8px' }}>{error}</div>}

        {loading && runs.length === 0 && artifacts.length === 0 && <div className="empty-state">Loading...</div>}

        <div className="field-label" style={{ marginTop: '4px' }}>Runs (from database)</div>
        {!loading && runs.length === 0 && <div className="empty-state">No backup runs.</div>}
        {runs.map((run) => (
          <button
            key={`run-${run.backupId}`}
            type="button"
            className={`session-card-selected session-card ${selectedId === run.backupId ? 'selected' : ''}`}
            onClick={() => setSelectedId(run.backupId)}
          >
            <span className="session-id">{run.backupType} · {run.status}</span>
            <span className="session-meta"><span>{formatDate(run.startedAt)}</span></span>
            <span className="session-meta"><span>{run.backupId}</span></span>
          </button>
        ))}

        <div className="field-label" style={{ marginTop: '10px' }}>Artifacts (from storage)</div>
        {!loading && artifacts.length === 0 && <div className="empty-state">No backup artifacts found under storage root.</div>}
        {artifacts.map((artifact) => (
          <button
            key={`artifact-${artifact.backupId}-${artifact.artifactPath}`}
            type="button"
            className={`session-card-selected session-card ${selectedId === artifact.backupId ? 'selected' : ''}`}
            onClick={() => setSelectedId(artifact.backupId)}
          >
            <span className="session-id">{artifact.backupType} · artifact</span>
            <span className="session-meta"><span>{formatDate(artifact.createdAtUtc)}</span></span>
            <span className="session-meta"><span>{artifact.backupId}</span></span>
          </button>
        ))}
      </aside>

      <main className="manager-main" style={{ overflow: 'auto', display: 'block' }}>
        <div className="chat-header">
          <div>
            <h2>Backup Configuration</h2>
            <p>Scheduler controls and storage path for generated artifacts.</p>
          </div>
        </div>

        <div className="agent-config" style={{ paddingTop: '12px', gap: '6px' }}>
          <label className="tool-toggle">
            <input type="checkbox" checked={formEnabled} onChange={(e) => setFormEnabled(e.target.checked)} />
            Scheduler enabled
          </label>

          <label className="field-label" htmlFor="backup-timezone">Timezone</label>
          <input id="backup-timezone" className="text-input" value={formTimezone} onChange={(e) => setFormTimezone(e.target.value)} />

          <label className="field-label" htmlFor="backup-daily-time">Daily Time (HH:mm:ss)</label>
          <input id="backup-daily-time" className="text-input" value={formDailyTime} onChange={(e) => setFormDailyTime(e.target.value)} />

          <label className="field-label" htmlFor="backup-full-every-n">Full Every N Runs</label>
          <input id="backup-full-every-n" className="text-input" value={formFullEveryN} onChange={(e) => setFormFullEveryN(e.target.value)} />

          <label className="field-label" htmlFor="backup-retention-days">Retention Days (optional)</label>
          <input id="backup-retention-days" className="text-input" value={formRetentionDays} onChange={(e) => setFormRetentionDays(e.target.value)} />

          <label className="field-label" htmlFor="backup-retention-full-chains">Retention Full Chains (optional)</label>
          <input id="backup-retention-full-chains" className="text-input" value={formRetentionFullChains} onChange={(e) => setFormRetentionFullChains(e.target.value)} />

          <label className="tool-toggle">
            <input type="checkbox" checked={formStrictRestoreDefault} onChange={(e) => setFormStrictRestoreDefault(e.target.checked)} />
            Strict restore by default
          </label>

          <label className="field-label" htmlFor="backup-storage-root">Storage Root</label>
          <input id="backup-storage-root" className="text-input" value={formStorageRoot} onChange={(e) => setFormStorageRoot(e.target.value)} />

          <div className="inline-actions" style={{ marginTop: '10px' }}>
            <button type="button" className="button primary" onClick={() => void saveConfig()} disabled={savingConfig || loading}>
              {savingConfig ? 'Saving...' : 'Save Config'}
            </button>
          </div>
        </div>

        <div className="chat-header" style={{ marginTop: '16px' }}>
          <div>
            <h2>Selected Backup</h2>
            <p>Restore from a DB-backed run or from an artifact discovered on disk.</p>
          </div>
        </div>

        {!selectedItem ? (
          <div className="empty-state" style={{ padding: '12px 0' }}>Select a run or artifact from the sidebar.</div>
        ) : (
          <div className="agent-config" style={{ paddingTop: '12px', gap: '6px' }}>
            {selectedItem.kind === 'run' ? (
              <>
                <span className="session-meta"><span>Source</span><span>Run row</span></span>
                <span className="session-meta"><span>Status</span><span>{selectedItem.run.status}</span></span>
                <span className="session-meta"><span>Type</span><span>{selectedItem.run.backupType}</span></span>
                <span className="session-meta"><span>Backup ID</span><span>{selectedItem.run.backupId}</span></span>
                <span className="session-meta"><span>Started</span><span>{formatDate(selectedItem.run.startedAt)}</span></span>
                <span className="session-meta"><span>Completed</span><span>{formatDate(selectedItem.run.completedAt)}</span></span>
                <span className="session-meta"><span>Artifact</span><span>{selectedItem.run.artifactPath ?? '—'}</span></span>
                {selectedItem.run.errorMessage && <div className="error-text">Run error: {selectedItem.run.errorMessage}</div>}
              </>
            ) : (
              <>
                <span className="session-meta"><span>Source</span><span>Artifact scan</span></span>
                <span className="session-meta"><span>Type</span><span>{selectedItem.artifact.backupType}</span></span>
                <span className="session-meta"><span>Backup ID</span><span>{selectedItem.artifact.backupId}</span></span>
                <span className="session-meta"><span>Created</span><span>{formatDate(selectedItem.artifact.createdAtUtc)}</span></span>
                <span className="session-meta"><span>Artifact</span><span>{selectedItem.artifact.artifactPath}</span></span>
              </>
            )}

            <div className="inline-actions" style={{ marginTop: '10px' }}>
              <button type="button" className="button ghost" onClick={() => void restoreSelected(true)} disabled={!!restoringId}>
                {restoringId === (selectedItem.kind === 'run' ? selectedItem.run.backupId : selectedItem.artifact.backupId) ? 'Restoring...' : 'Restore (Strict)'}
              </button>
              <button type="button" className="button ghost" onClick={() => void restoreSelected(false)} disabled={!!restoringId}>
                Restore (Relaxed)
              </button>
            </div>

            {selectedItem.kind === 'run' && (
              <>
                <label className="tool-toggle" style={{ marginTop: '10px' }}>
                  <input
                    type="checkbox"
                    checked={deleteArtifactWithRun}
                    onChange={(e) => setDeleteArtifactWithRun(e.target.checked)}
                  />
                  Also delete artifact file on disk
                </label>
                <div className="inline-actions" style={{ marginTop: '6px' }}>
                  <button
                    type="button"
                    className="button ghost"
                    onClick={() => void deleteSelectedRun()}
                    disabled={deletingRunId === selectedItem.run.backupId}
                  >
                    {deletingRunId === selectedItem.run.backupId ? 'Deleting...' : 'Delete Run'}
                  </button>
                </div>
              </>
            )}
          </div>
        )}
      </main>
    </div>
  )
}
