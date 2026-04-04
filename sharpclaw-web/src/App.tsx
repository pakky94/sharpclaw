import { useState } from 'react'
import { AgentConsolePage } from './app/pages/AgentConsolePage'
import { AgentManagementPage } from './app/pages/AgentManagementPage'
import './app/pages/AgentConsolePage.css'

function App() {
  const [page, setPage] = useState<'sessions' | 'agents'>('sessions')
  const [sessionsHasUnsaved, setSessionsHasUnsaved] = useState(false)
  const [agentsHasUnsaved, setAgentsHasUnsaved] = useState(false)

  return (
    <div className="app-root">
      <header className="top-nav">
        <button type="button" className={`button ${page === 'sessions' ? 'primary' : 'ghost'}`} onClick={() => setPage('sessions')}>
          Sessions{sessionsHasUnsaved ? ' *' : ''}
        </button>
        <button type="button" className={`button ${page === 'agents' ? 'primary' : 'ghost'}`} onClick={() => setPage('agents')}>
          Agents{agentsHasUnsaved ? ' *' : ''}
        </button>
      </header>
      <div className="app-content">
        <section className={`app-content-pane ${page === 'sessions' ? 'active' : ''}`} aria-hidden={page !== 'sessions'}>
          <AgentConsolePage onUnsavedChange={setSessionsHasUnsaved} />
        </section>
        <section className={`app-content-pane ${page === 'agents' ? 'active' : ''}`} aria-hidden={page !== 'agents'}>
          <AgentManagementPage onUnsavedChange={setAgentsHasUnsaved} />
        </section>
      </div>
    </div>
  )
}

export default App
