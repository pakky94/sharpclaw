import { useState } from 'react'
import { AgentConsolePage } from './app/pages/AgentConsolePage'
import { AgentManagementPage } from './app/pages/AgentManagementPage'
import './app/pages/AgentConsolePage.css'

function App() {
  const [page, setPage] = useState<'sessions' | 'agents'>('sessions')

  return (
    <div className="app-root">
      <header className="top-nav">
        <button type="button" className={`button ${page === 'sessions' ? 'primary' : 'ghost'}`} onClick={() => setPage('sessions')}>
          Sessions
        </button>
        <button type="button" className={`button ${page === 'agents' ? 'primary' : 'ghost'}`} onClick={() => setPage('agents')}>
          Agents
        </button>
      </header>
      <div className="app-content">{page === 'sessions' ? <AgentConsolePage /> : <AgentManagementPage />}</div>
    </div>
  )
}

export default App
