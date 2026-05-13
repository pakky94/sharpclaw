import { useState } from 'react'
import { AgentConsolePage } from './app/pages/AgentConsolePage'
import { AgentManagementPage } from './app/pages/AgentManagementPage'
import { WorkspaceManagementPage } from './app/pages/WorkspaceManagementPage'
import { ScheduledJobsPage } from './app/pages/ScheduledJobsPage'
import { ChannelsPage } from './app/pages/ChannelsPage'
import './app/pages/AgentConsolePage.css'

function App() {
  const [page, setPage] = useState<'sessions' | 'agents' | 'workspaces' | 'jobs' | 'channels'>('sessions')
  const [sessionsHasUnsaved, setSessionsHasUnsaved] = useState(false)
  const [agentsHasUnsaved, setAgentsHasUnsaved] = useState(false)
  const [workspacesHasUnsaved, setWorkspacesHasUnsaved] = useState(false)

  return (
    <div className="app-root">
      <header className="top-nav">
        <button type="button" className={`button ${page === 'sessions' ? 'primary' : 'ghost'}`} onClick={() => setPage('sessions')}>
          Sessions{sessionsHasUnsaved ? ' *' : ''}
        </button>
        <button type="button" className={`button ${page === 'agents' ? 'primary' : 'ghost'}`} onClick={() => setPage('agents')}>
          Agents{agentsHasUnsaved ? ' *' : ''}
        </button>
        <button type="button" className={`button ${page === 'workspaces' ? 'primary' : 'ghost'}`} onClick={() => setPage('workspaces')}>
          Workspaces{workspacesHasUnsaved ? ' *' : ''}
        </button>
        <button type="button" className={`button ${page === 'jobs' ? 'primary' : 'ghost'}`} onClick={() => setPage('jobs')}>
          Jobs
        </button>
        <button type="button" className={`button ${page === 'channels' ? 'primary' : 'ghost'}`} onClick={() => setPage('channels')}>
          Channels
        </button>
      </header>
      <div className="app-content">
        <section className={`app-content-pane ${page === 'sessions' ? 'active' : ''}`} aria-hidden={page !== 'sessions'}>
          <AgentConsolePage onUnsavedChange={setSessionsHasUnsaved} />
        </section>
        <section className={`app-content-pane ${page === 'agents' ? 'active' : ''}`} aria-hidden={page !== 'agents'}>
          <AgentManagementPage onUnsavedChange={setAgentsHasUnsaved} />
        </section>
        <section className={`app-content-pane ${page === 'workspaces' ? 'active' : ''}`} aria-hidden={page !== 'workspaces'}>
          <WorkspaceManagementPage onUnsavedChange={setWorkspacesHasUnsaved} />
        </section>
        <section className={`app-content-pane ${page === 'jobs' ? 'active' : ''}`} aria-hidden={page !== 'jobs'}>
          <ScheduledJobsPage />
        </section>
        <section className={`app-content-pane ${page === 'channels' ? 'active' : ''}`} aria-hidden={page !== 'channels'}>
          <ChannelsPage />
        </section>
      </div>
    </div>
  )
}

export default App
