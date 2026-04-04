import { useEffect, useMemo, useState } from 'react'
import { API_BASE_URL, fetchJson } from '../services/chatApi'
import type { AgentConfig, AgentFileSummary } from '../types/chat'
import { asErrorMessage } from '../utils/chatUtils'
import './AgentConsolePage.css'

const DEFAULT_MODEL = 'openai/gpt-oss-20b'

type AgentManagementPageProps = {
  onUnsavedChange?: (hasUnsaved: boolean) => void
}

type FileDraft = {
  path: string
  content: string
  dirty: boolean
  originalPath: string | null
  originalContent: string | null
}

function getExistingFileDraftKey(agentId: number, path: string) {
  return `existing:${agentId}:${path}`
}

function getNewFileDraftKey(agentId: number) {
  return `new:${agentId}`
}

export function AgentManagementPage({ onUnsavedChange }: AgentManagementPageProps) {
  const [agents, setAgents] = useState<AgentConfig[]>([])
  const [selectedAgentId, setSelectedAgentId] = useState<number | null>(null)
  const [agentName, setAgentName] = useState('')
  const [agentModel, setAgentModel] = useState(DEFAULT_MODEL)
  const [agentTemperature, setAgentTemperature] = useState('0.1')
  const [files, setFiles] = useState<AgentFileSummary[]>([])
  const [selectedFilePath, setSelectedFilePath] = useState<string | null>(null)
  const [activeDraftKey, setActiveDraftKey] = useState<string | null>(null)
  const [fileDrafts, setFileDrafts] = useState<Record<string, FileDraft>>({})
  const [fileLoading, setFileLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const activeDraft = activeDraftKey ? fileDrafts[activeDraftKey] ?? null : null
  const filePath = activeDraft?.path ?? ''
  const fileContent = activeDraft?.content ?? ''
  const fileDirty = activeDraft?.dirty ?? false
  const hasUnsavedDrafts = useMemo(() => Object.values(fileDrafts).some((draft) => draft.dirty), [fileDrafts])
  const unsavedFileKeys = useMemo(() => {
    const next = new Set<string>()
    for (const [key, draft] of Object.entries(fileDrafts)) {
      if (draft.dirty) {
        next.add(key)
      }
    }
    return next
  }, [fileDrafts])
  const unsavedNewFileForSelectedAgent =
    selectedAgentId !== null && unsavedFileKeys.has(getNewFileDraftKey(selectedAgentId))

  useEffect(() => {
    void loadAgents()
  }, [])

  useEffect(() => {
    if (selectedAgentId === null) {
      setFiles([])
      resetEditor()
      return
    }

    void refreshFiles(selectedAgentId)
  }, [selectedAgentId])

  useEffect(() => {
    onUnsavedChange?.(hasUnsavedDrafts)
  }, [hasUnsavedDrafts, onUnsavedChange])

  function resetEditor() {
    setSelectedFilePath(null)
    setActiveDraftKey(null)
  }

  function applyAgentForm(agent: AgentConfig) {
    setAgentName(agent.name)
    setAgentModel(agent.llmModel)
    setAgentTemperature(String(agent.temperature))
  }

  async function loadAgents(preferredAgentId?: number) {
    try {
      setError(null)
      const data = await fetchJson<{ agents: AgentConfig[] }>(`${API_BASE_URL}/agents`)
      setAgents(data.agents)

      if (data.agents.length === 0) {
        setSelectedAgentId(null)
        setAgentName('')
        setAgentModel(DEFAULT_MODEL)
        setAgentTemperature('0.1')
        return
      }

      const preferred =
        preferredAgentId !== undefined
          ? data.agents.find((agent) => agent.id === preferredAgentId)
          : data.agents.find((agent) => agent.id === selectedAgentId)
      const selected = preferred ?? data.agents[0]
      setSelectedAgentId(selected.id)
      applyAgentForm(selected)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function refreshFiles(agentId: number) {
    try {
      setError(null)
      const data = await fetchJson<{ agentId: number; files: AgentFileSummary[] }>(`${API_BASE_URL}/agents/${agentId}/files`)
      setFiles(data.files)

      if (selectedFilePath && !data.files.some((file) => file.name === selectedFilePath)) {
        resetEditor()
      }
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function createAgent() {
    try {
      setError(null)
      const created = await fetchJson<AgentConfig>(`${API_BASE_URL}/agents`, {
        method: 'POST',
        body: JSON.stringify({
          name: 'New Agent',
          llmModel: DEFAULT_MODEL,
          temperature: 0.1,
        }),
      })

      resetEditor()
      await loadAgents(created.id)
      await refreshFiles(created.id)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function saveAgent() {
    if (selectedAgentId === null) {
      return
    }

    const temperature = Number.parseFloat(agentTemperature)
    if (Number.isNaN(temperature)) {
      setError('Temperature must be a number.')
      return
    }

    try {
      setError(null)
      await fetchJson<AgentConfig>(`${API_BASE_URL}/agents/${selectedAgentId}`, {
        method: 'PUT',
        body: JSON.stringify({
          name: agentName.trim(),
          llmModel: agentModel.trim() || DEFAULT_MODEL,
          temperature,
        }),
      })
      await loadAgents(selectedAgentId)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function loadFile(path: string) {
    if (selectedAgentId === null) {
      return
    }

    const draftKey = getExistingFileDraftKey(selectedAgentId, path)
    const existingDraft = fileDrafts[draftKey]
    if (existingDraft) {
      setSelectedFilePath(path)
      setActiveDraftKey(draftKey)
      return
    }

    try {
      setError(null)
      setFileLoading(true)
      const data = await fetchJson<{ path: string; content: string }>(
        `${API_BASE_URL}/agents/${selectedAgentId}/file?path=${encodeURIComponent(path)}`,
      )
      setSelectedFilePath(data.path)
      setFileDrafts((prev) => ({
        ...prev,
        [draftKey]: {
          path: data.path,
          content: data.content,
          dirty: false,
          originalPath: data.path,
          originalContent: data.content,
        },
      }))
      setActiveDraftKey(draftKey)
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setFileLoading(false)
    }
  }

  async function saveFile() {
    if (selectedAgentId === null || !activeDraftKey || !filePath.trim()) {
      return
    }

    try {
      setError(null)
      setFileLoading(true)
      const path = filePath.trim()
      await fetchJson<{ agentId: number; path: string }>(`${API_BASE_URL}/agents/${selectedAgentId}/file`, {
        method: 'PUT',
        body: JSON.stringify({ path, content: fileContent }),
      })
      const savedDraftKey = getExistingFileDraftKey(selectedAgentId, path)
      setFileDrafts((prev) => {
        const next = { ...prev }
        if (activeDraftKey !== savedDraftKey) {
          delete next[activeDraftKey]
        }
        next[savedDraftKey] = {
          path,
          content: fileContent,
          dirty: false,
          originalPath: path,
          originalContent: fileContent,
        }
        return next
      })
      setActiveDraftKey(savedDraftKey)
      setSelectedFilePath(path)
      await refreshFiles(selectedAgentId)
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setFileLoading(false)
    }
  }

  async function deleteFile() {
    if (selectedAgentId === null || !selectedFilePath) {
      return
    }

    try {
      setError(null)
      const fileDraftKey = getExistingFileDraftKey(selectedAgentId, selectedFilePath)
      await fetchJson<{ agentId: number; path: string }>(
        `${API_BASE_URL}/agents/${selectedAgentId}/file?path=${encodeURIComponent(selectedFilePath)}`,
        { method: 'DELETE' },
      )
      setFileDrafts((prev) => {
        const next = { ...prev }
        delete next[fileDraftKey]
        return next
      })
      resetEditor()
      await refreshFiles(selectedAgentId)
    } catch (e) {
      setError(asErrorMessage(e))
    }
  }

  async function reloadFile() {
    if (selectedAgentId === null || !selectedFilePath) {
      return
    }

    const draftKey = getExistingFileDraftKey(selectedAgentId, selectedFilePath)

    try {
      setError(null)
      setFileLoading(true)
      const data = await fetchJson<{ path: string; content: string }>(
        `${API_BASE_URL}/agents/${selectedAgentId}/file?path=${encodeURIComponent(selectedFilePath)}`,
      )
      setSelectedFilePath(data.path)
      setActiveDraftKey(draftKey)
      setFileDrafts((prev) => ({
        ...prev,
        [draftKey]: {
          path: data.path,
          content: data.content,
          dirty: false,
          originalPath: data.path,
          originalContent: data.content,
        },
      }))
    } catch (e) {
      setError(asErrorMessage(e))
    } finally {
      setFileLoading(false)
    }
  }

  function discardUnsavedChanges() {
    if (!activeDraftKey) {
      return
    }

    setError(null)
    setFileDrafts((prev) => {
      const current = prev[activeDraftKey]
      if (!current) {
        return prev
      }

      if (current.originalPath === null || current.originalContent === null) {
        return {
          ...prev,
          [activeDraftKey]: {
            ...current,
            path: '',
            content: '',
            dirty: false,
          },
        }
      }

      return {
        ...prev,
        [activeDraftKey]: {
          ...current,
          path: current.originalPath,
          content: current.originalContent,
          dirty: false,
        },
      }
    })
  }

  return (
    <div className="manager-shell">
      <aside className="manager-sidebar">
        <div className="panel-header">
          <h1>Agents</h1>
          <p>Create, configure, and manage files.</p>
        </div>

        <label className="field-label" htmlFor="agent-select-manager">
          Agent
        </label>
        <div className="inline-actions">
          <select
            id="agent-select-manager"
            className="text-input"
            value={selectedAgentId ?? ''}
            onChange={(event) => {
              const id = Number(event.target.value)
              const selected = agents.find((agent) => agent.id === id)
              if (selected) {
                setSelectedAgentId(id)
                applyAgentForm(selected)
                resetEditor()
              }
            }}
          >
            {agents.map((agent) => (
              <option key={agent.id} value={agent.id}>
                {agent.id} - {agent.name}
              </option>
            ))}
          </select>
          <button type="button" className="button ghost" onClick={() => void createAgent()}>
            New
          </button>
        </div>

        <div className="agent-config">
          <label className="field-label" htmlFor="agent-name-input-manager">
            Name
          </label>
          <input
            id="agent-name-input-manager"
            className="text-input"
            value={agentName}
            onChange={(e) => setAgentName(e.target.value)}
          />

          <label className="field-label" htmlFor="agent-model-input-manager">
            LLM Model
          </label>
          <input
            id="agent-model-input-manager"
            className="text-input"
            value={agentModel}
            onChange={(e) => setAgentModel(e.target.value)}
          />

          <label className="field-label" htmlFor="agent-temperature-input-manager">
            Temperature
          </label>
          <input
            id="agent-temperature-input-manager"
            className="text-input"
            type="number"
            step="0.1"
            min="0"
            max="2"
            value={agentTemperature}
            onChange={(e) => setAgentTemperature(e.target.value)}
          />

          <button type="button" className="button primary" onClick={() => void saveAgent()} disabled={!selectedAgentId}>
            Save Agent
          </button>
        </div>

        <div className="files-header">
          <h3>Files{unsavedNewFileForSelectedAgent ? ' *' : ''}</h3>
          <div className="inline-actions">
            <button
              type="button"
              className="button ghost"
              onClick={() => selectedAgentId !== null && void refreshFiles(selectedAgentId)}
              disabled={!selectedAgentId}
            >
              Refresh
            </button>
            <button
              type="button"
              className="button ghost"
              onClick={() => {
                if (selectedAgentId === null) {
                  return
                }
                const newDraftKey = getNewFileDraftKey(selectedAgentId)
                setSelectedFilePath(null)
                setActiveDraftKey(newDraftKey)
                setFileDrafts((prev) => ({
                  ...prev,
                  [newDraftKey]:
                    prev[newDraftKey] ??
                    { path: '', content: '', dirty: false, originalPath: null, originalContent: null },
                }))
              }}
              disabled={!selectedAgentId}
            >
              New
            </button>
            <button type="button" className="button ghost" onClick={() => void deleteFile()} disabled={!selectedFilePath || !selectedAgentId}>
              Delete
            </button>
          </div>
        </div>

        <div className="files-list">
          {files.map((file) => {
            const isSelected = selectedFilePath === file.name
            const fileDraftKey = selectedAgentId !== null ? getExistingFileDraftKey(selectedAgentId, file.name) : null
            const hasUnsaved = fileDraftKey ? unsavedFileKeys.has(fileDraftKey) : false
            return (
              <button
                key={file.name}
                type="button"
                className={`session-card ${isSelected ? 'selected' : ''}`}
                onClick={() => {
                  if (isSelected) {
                    resetEditor()
                    return
                  }
                  void loadFile(file.name)
                }}
              >
                <div className="session-id">
                  {file.name}
                  {hasUnsaved ? ' *' : ''}
                </div>
              </button>
            )
          })}
          {files.length === 0 && <div className="empty-state">No files yet.</div>}
        </div>
      </aside>

      <main className="manager-main">
        <div className="chat-header">
          <div>
            <h2>{selectedFilePath ?? 'No File Selected'}{fileDirty ? ' *' : ''}</h2>
            <p>{selectedFilePath ? 'Editing selected file.' : 'Select a file from the sidebar or click New.'}</p>
          </div>
        </div>

        <label className="field-label" htmlFor="file-path-input-manager">
          File Path
        </label>
        <input
          id="file-path-input-manager"
          className="text-input"
          value={filePath}
          onChange={(e) => {
            if (!activeDraftKey) {
              return
            }
            const value = e.target.value
            setFileDrafts((prev) => {
              const current = prev[activeDraftKey]
              return {
                ...prev,
                [activeDraftKey]: {
                  path: value,
                  content: current?.content ?? '',
                  dirty: true,
                  originalPath: current?.originalPath ?? null,
                  originalContent: current?.originalContent ?? null,
                },
              }
            })
          }}
          disabled={fileLoading || !selectedAgentId || !activeDraftKey}
        />

        <textarea
          className="composer-input file-editor-main"
          value={fileContent}
          onChange={(e) => {
            if (!activeDraftKey) {
              return
            }
            const value = e.target.value
            setFileDrafts((prev) => {
              const current = prev[activeDraftKey]
              return {
                ...prev,
                [activeDraftKey]: {
                  path: current?.path ?? '',
                  content: value,
                  dirty: true,
                  originalPath: current?.originalPath ?? null,
                  originalContent: current?.originalContent ?? null,
                },
              }
            })
          }}
          disabled={fileLoading || !selectedAgentId || !activeDraftKey}
        />

        <div className="composer-footer">
          {error && <span className="error-text">{error}</span>}
          <div className="inline-actions">
            <button
              type="button"
              className="button ghost"
              onClick={discardUnsavedChanges}
              disabled={!selectedAgentId || fileLoading || !activeDraftKey || !fileDirty}
            >
              Discard
            </button>
            <button
              type="button"
              className="button ghost"
              onClick={() => void reloadFile()}
              disabled={!selectedAgentId || fileLoading || !selectedFilePath}
            >
              Reload
            </button>
            <button
              type="button"
              className="button primary"
              onClick={() => void saveFile()}
              disabled={!selectedAgentId || fileLoading || !filePath.trim() || !fileDirty}
            >
              Save File
            </button>
          </div>
        </div>
      </main>
    </div>
  )
}
