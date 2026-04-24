package tui

import (
	"context"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/charmbracelet/bubbles/textinput"
	"github.com/charmbracelet/bubbles/viewport"
	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/lipgloss"

	"sharpclaw-tui/internal/api"
	"sharpclaw-tui/internal/compose"
	"sharpclaw-tui/internal/config"
)

var (
	titleStyle       = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color("12"))
	mutedStyle       = lipgloss.NewStyle().Foreground(lipgloss.Color("8"))
	errorStyle       = lipgloss.NewStyle().Foreground(lipgloss.Color("9"))
	activeTabStyle   = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color("10"))
	inactiveTabStyle = lipgloss.NewStyle().Foreground(lipgloss.Color("7"))
	panelStyle       = lipgloss.NewStyle().Border(lipgloss.NormalBorder()).Padding(0, 1)
)

type tab int

const (
	tabChat tab = iota
	tabAgents
	tabWorkspaces
	tabCompose
)

type chatMessage struct {
	role string
	text string
}

type model struct {
	cfg    config.Config
	client *api.Client
	runner compose.Runner

	width  int
	height int

	activeTab tab

	agents        []api.AgentConfig
	selectedAgent int

	sessions        []api.SessionSummary
	selectedSession int

	messages      []chatMessage
	messageOutput viewport.Model
	chatInput     textinput.Model
	chatFocus     int

	polling bool

	approvals         []api.ApprovalEvent
	selectedApproval  int
	approvalsSession  textinput.Model
	workspaceName     textinput.Model
	workspaceRootPath textinput.Model

	workspaces         []api.WorkspaceConfig
	selectedWorkspace  int
	agentWorkspaces    []api.AgentWorkspaceEntry
	selectedAssignment int

	agentNameInput  textinput.Model
	agentModelInput textinput.Model
	agentTempInput  textinput.Model
	agentFieldFocus int
	creatingAgent   bool
	lastSavedAgent  int64

	composeOutput viewport.Model
	status        string
	err           string

	workspaceFieldFocus int
}

type errMsg struct{ err error }
type agentsLoadedMsg struct{ agents []api.AgentConfig }
type sessionsLoadedMsg struct{ sessions []api.SessionSummary }
type historyLoadedMsg struct {
	history  api.SessionHistoryResponse
	session  string
	fromPoll bool
}
type sendDoneMsg struct {
	sessionID string
}
type workspacesLoadedMsg struct{ workspaces []api.WorkspaceConfig }
type agentWorkspacesLoadedMsg struct{ entries []api.AgentWorkspaceEntry }
type approvalsLoadedMsg struct{ approvals []api.ApprovalEvent }
type composeResultMsg struct{ output string }
type pollTickMsg struct{}
type agentSavedMsg struct{ agent api.AgentConfig }
type workspaceSavedMsg struct{ workspace api.WorkspaceConfig }

func tabAtX(x int) tab {
	start := len("SharpClaw TUI") + 2
	tabNames := []string{"Chat", "Agents", "Workspaces", "Compose"}
	for i, name := range tabNames {
		end := start + len(name)
		if x >= start && x < end {
			return tab(i)
		}
		start = end + 2
	}
	return tabChat
}

func (m model) chatSectionHeights() (metaTotalHeight, messagesTotalHeight, approvalsTotalHeight, inputTotalHeight int) {
	totalAvailable := max(10, m.height-3)
	metaTotalHeight = 4
	inputTotalHeight = 6
	approvalsRows := len(strings.Split(m.renderApprovals(), "\n"))
	approvalsTotalHeight = min(7, max(4, approvalsRows+2))
	messagesTotalHeight = totalAvailable - metaTotalHeight - approvalsTotalHeight - inputTotalHeight
	if messagesTotalHeight < 5 {
		shortage := 5 - messagesTotalHeight
		for shortage > 0 && approvalsTotalHeight > 4 {
			approvalsTotalHeight--
			shortage--
		}
		for shortage > 0 && inputTotalHeight > 4 {
			inputTotalHeight--
			shortage--
		}
		messagesTotalHeight = 5
	}
	return
}

func (m *model) handleChatMouseClick(y int) {
	bodyY := y - 1
	if bodyY < 0 {
		return
	}
	metaH, messagesH, approvalsH, _ := m.chatSectionHeights()

	if bodyY < metaH {
		return
	}
	bodyY -= metaH
	if bodyY < messagesH {
		return
	}
	bodyY -= messagesH
	if bodyY < approvalsH {
		row := bodyY - 1
		if row >= 0 && row < len(m.approvals) {
			m.selectedApproval = row
		}
		return
	}
	bodyY -= approvalsH
	inputRow := bodyY - 1
	if inputRow <= 1 {
		m.chatFocus = 0
	} else {
		m.chatFocus = 1
	}
	m.applyFocus()
}

func (m *model) handleAgentsMouseClick(x, y int) []tea.Cmd {
	var cmds []tea.Cmd
	bodyY := y - 1
	panelH := max(8, m.height-10)
	if bodyY < 0 || bodyY >= panelH {
		return nil
	}

	innerY := bodyY - 1
	leftW := max(30, m.width/2-2)
	if x < leftW {
		if innerY >= 0 && innerY < len(m.agents) {
			m.selectedAgent = innerY
			m.syncAgentInputsFromSelection()
			selectedID := m.selectedAgentID()
			if selectedID != 0 {
				cmds = append(cmds, m.loadSessionsCmd(selectedID), m.loadAgentWorkspacesCmd(selectedID))
			}
		}
		return cmds
	}

	switch innerY {
	case 1:
		m.agentFieldFocus = 0
		m.applyFocus()
	case 2:
		m.agentFieldFocus = 1
		m.applyFocus()
	case 3:
		m.agentFieldFocus = 2
		m.applyFocus()
	}
	return cmds
}

func (m *model) handleWorkspacesMouseClick(x, y int) []tea.Cmd {
	bodyY := y - 1
	panelH := max(8, m.height-10)
	if bodyY < 0 || bodyY >= panelH {
		return nil
	}
	innerY := bodyY - 1

	leftW := max(30, m.width/2-2)
	if x < leftW {
		if innerY >= 0 && innerY < len(m.workspaces) {
			m.selectedWorkspace = innerY
			m.syncWorkspaceInputsFromSelection()
		}
		return nil
	}

	switch innerY {
	case 1:
		m.workspaceFieldFocus = 0
		m.applyFocus()
		return nil
	case 2:
		m.workspaceFieldFocus = 1
		m.applyFocus()
		return nil
	}

	assignedStart := 5
	row := innerY - assignedStart
	if row >= 0 && row < len(m.agentWorkspaces) {
		m.selectedAssignment = row
	}
	return nil
}

func New(cfg config.Config, runner compose.Runner) tea.Model {
	chatInput := textinput.New()
	chatInput.Placeholder = "Type message and press Enter"
	chatInput.Focus()
	chatInput.CharLimit = 0
	chatInput.Width = 80

	approvalSession := textinput.New()
	approvalSession.Placeholder = "session-id"
	approvalSession.Width = 40

	workspaceName := textinput.New()
	workspaceName.Placeholder = "workspace-name"
	workspaceName.Width = 30

	workspaceRoot := textinput.New()
	workspaceRoot.Placeholder = "optional root path"
	workspaceRoot.Width = 30

	agentName := textinput.New()
	agentName.Placeholder = "agent name"
	agentName.Width = 24

	agentModel := textinput.New()
	agentModel.Placeholder = "openai/gpt-oss-20b"
	agentModel.Width = 24

	agentTemp := textinput.New()
	agentTemp.Placeholder = "0.1"
	agentTemp.Width = 8

	msgVp := viewport.New(80, 20)
	composeVp := viewport.New(80, 20)

	m := model{
		cfg:               cfg,
		client:            api.NewClient(cfg.APIBaseURL),
		runner:            runner,
		activeTab:         tabChat,
		selectedAgent:     0,
		selectedSession:   0,
		selectedWorkspace: 0,
		messageOutput:     msgVp,
		chatInput:         chatInput,
		approvalsSession:  approvalSession,
		workspaceName:     workspaceName,
		workspaceRootPath: workspaceRoot,
		agentNameInput:    agentName,
		agentModelInput:   agentModel,
		agentTempInput:    agentTemp,
		composeOutput:     composeVp,
		status:            "Loading agents...",
	}
	m.applyFocus()
	return m
}

func (m model) Init() tea.Cmd {
	return tea.Batch(m.loadAgentsCmd(), m.loadWorkspacesCmd())
}

func (m model) loadAgentsCmd() tea.Cmd {
	return func() tea.Msg {
		agents, err := m.client.ListAgents(context.Background())
		if err != nil {
			return errMsg{err: err}
		}
		return agentsLoadedMsg{agents: agents}
	}
}

func (m model) loadSessionsCmd(agentID int64) tea.Cmd {
	return func() tea.Msg {
		sessions, err := m.client.ListAgentSessions(context.Background(), agentID)
		if err != nil {
			return errMsg{err: err}
		}
		return sessionsLoadedMsg{sessions: sessions}
	}
}

func (m model) loadHistoryCmd(sessionID string, fromPoll bool) tea.Cmd {
	return func() tea.Msg {
		history, err := m.client.SessionHistory(context.Background(), sessionID)
		if err != nil {
			return errMsg{err: err}
		}
		return historyLoadedMsg{history: history, session: sessionID, fromPoll: fromPoll}
	}
}

func (m model) loadWorkspacesCmd() tea.Cmd {
	return func() tea.Msg {
		workspaces, err := m.client.ListWorkspaces(context.Background())
		if err != nil {
			return errMsg{err: err}
		}
		return workspacesLoadedMsg{workspaces: workspaces}
	}
}

func (m model) loadAgentWorkspacesCmd(agentID int64) tea.Cmd {
	return func() tea.Msg {
		entries, err := m.client.AgentWorkspaces(context.Background(), agentID)
		if err != nil {
			return errMsg{err: err}
		}
		return agentWorkspacesLoadedMsg{entries: entries}
	}
}

func (m model) loadApprovalsCmd(sessionID string) tea.Cmd {
	return func() tea.Msg {
		items, err := m.client.PendingApprovals(context.Background(), sessionID)
		if err != nil {
			return errMsg{err: err}
		}
		return approvalsLoadedMsg{approvals: items}
	}
}

func (m model) sendChatCmd(agentID int64, sessionID, text string) tea.Cmd {
	return func() tea.Msg {
		ctx := context.Background()
		resolvedSession := sessionID
		if strings.TrimSpace(resolvedSession) == "" {
			created, err := m.client.CreateSession(ctx, agentID)
			if err != nil {
				return errMsg{err: err}
			}
			resolvedSession = created.SessionID
		}

		_, err := m.client.SendMessage(ctx, resolvedSession, text)
		if err != nil {
			return errMsg{err: err}
		}

		return sendDoneMsg{sessionID: resolvedSession}
	}
}

func (m model) pollCmd() tea.Cmd {
	return tea.Tick(800*time.Millisecond, func(time.Time) tea.Msg { return pollTickMsg{} })
}

func (m model) composeCmd(action string, services []string, build bool, volumes bool) tea.Cmd {
	return func() tea.Msg {
		var (
			out string
			err error
		)

		switch action {
		case "start":
			out, err = m.runner.Start(services, build)
		case "stop":
			out, err = m.runner.Stop(services, volumes)
		default:
			out, err = m.runner.Status()
		}

		if err != nil {
			return errMsg{err: err}
		}
		return composeResultMsg{output: out}
	}
}

func (m model) saveAgentCmd(id int64, create bool, name, modelName string, temp float64) tea.Cmd {
	return func() tea.Msg {
		var (
			agent api.AgentConfig
			err   error
		)
		if create {
			agent, err = m.client.CreateAgent(context.Background(), name, modelName, temp)
		} else {
			agent, err = m.client.UpdateAgent(context.Background(), id, name, modelName, temp)
		}
		if err != nil {
			return errMsg{err: err}
		}
		return agentSavedMsg{agent: agent}
	}
}

func (m model) saveWorkspaceCmd(name, root string) tea.Cmd {
	return func() tea.Msg {
		ws, err := m.client.UpsertWorkspace(context.Background(), name, root, nil, nil)
		if err != nil {
			return errMsg{err: err}
		}
		return workspaceSavedMsg{workspace: ws}
	}
}

func (m model) selectedAgentID() int64 {
	if len(m.agents) == 0 {
		return 0
	}
	idx := m.selectedAgent
	if idx < 0 {
		idx = 0
	}
	if idx >= len(m.agents) {
		idx = len(m.agents) - 1
	}
	return m.agents[idx].ID
}

func (m model) selectedSessionID() string {
	if len(m.sessions) == 0 {
		return ""
	}
	idx := m.selectedSession
	if idx < 0 {
		idx = 0
	}
	if idx >= len(m.sessions) {
		idx = len(m.sessions) - 1
	}
	return m.sessions[idx].SessionID
}

func (m *model) syncAgentInputsFromSelection() {
	if len(m.agents) == 0 {
		return
	}
	idx := m.selectedAgent
	if idx < 0 {
		idx = 0
	}
	if idx >= len(m.agents) {
		idx = len(m.agents) - 1
	}
	selected := m.agents[idx]
	m.agentNameInput.SetValue(selected.Name)
	m.agentModelInput.SetValue(selected.LlmModel)
	m.agentTempInput.SetValue(strconv.FormatFloat(selected.Temperature, 'f', 2, 64))
	m.creatingAgent = false
}

func (m *model) syncWorkspaceInputsFromSelection() {
	if len(m.workspaces) == 0 {
		return
	}
	idx := m.selectedWorkspace
	if idx < 0 {
		idx = 0
	}
	if idx >= len(m.workspaces) {
		idx = len(m.workspaces) - 1
	}
	selected := m.workspaces[idx]
	m.workspaceName.SetValue(selected.Name)
	m.workspaceRootPath.SetValue(selected.RootPath)
}

func (m *model) applyFocus() {
	m.chatInput.Blur()
	m.approvalsSession.Blur()
	m.agentNameInput.Blur()
	m.agentModelInput.Blur()
	m.agentTempInput.Blur()
	m.workspaceName.Blur()
	m.workspaceRootPath.Blur()

	switch m.activeTab {
	case tabChat:
		if m.chatFocus == 1 {
			m.approvalsSession.Focus()
		} else {
			m.chatFocus = 0
			m.chatInput.Focus()
		}
	case tabAgents:
		switch m.agentFieldFocus {
		case 1:
			m.agentModelInput.Focus()
		case 2:
			m.agentTempInput.Focus()
		default:
			m.agentFieldFocus = 0
			m.agentNameInput.Focus()
		}
	case tabWorkspaces:
		if m.workspaceFieldFocus == 1 {
			m.workspaceRootPath.Focus()
		} else {
			m.workspaceFieldFocus = 0
			m.workspaceName.Focus()
		}
	}
}

func (m *model) rebuildMessages(history api.SessionHistoryResponse) {
	items := make([]string, 0, len(history.Messages))
	m.messages = m.messages[:0]
	for _, msg := range history.Messages {
		text := ""
		if msg.Text != nil {
			text = *msg.Text
		}
		if strings.TrimSpace(text) == "" {
			for _, content := range msg.Contents {
				if content.Text != nil && strings.TrimSpace(*content.Text) != "" {
					text = *content.Text
					break
				}
				if content.ToolName != nil && *content.ToolName != "" {
					text = fmt.Sprintf("[tool:%s]", *content.ToolName)
				}
			}
		}
		if strings.TrimSpace(text) == "" {
			continue
		}
		m.messages = append(m.messages, chatMessage{role: msg.Role, text: text})
		items = append(items, fmt.Sprintf("[%s] %s", msg.Role, text))
	}
	m.messageOutput.SetContent(strings.Join(items, "\n\n"))
	m.messageOutput.GotoBottom()
}

func (m model) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	var cmds []tea.Cmd
	m.err = ""

	switch msg := msg.(type) {
	case tea.WindowSizeMsg:
		m.width = msg.Width
		m.height = msg.Height
		m.chatInput.Width = max(20, m.width-10)
		m.messageOutput.Width = max(30, m.width-6)
		m.messageOutput.Height = max(8, m.height-16)
		m.composeOutput.Width = max(30, m.width-6)
		m.composeOutput.Height = max(8, m.height-12)
	case errMsg:
		m.err = msg.err.Error()
		m.status = "Error"
		m.polling = false
	case agentsLoadedMsg:
		m.agents = msg.agents
		if len(m.agents) == 0 {
			m.status = "No agents found"
			break
		}
		if m.lastSavedAgent != 0 {
			for i := range m.agents {
				if m.agents[i].ID == m.lastSavedAgent {
					m.selectedAgent = i
					break
				}
			}
			m.lastSavedAgent = 0
		}
		if m.selectedAgent >= len(m.agents) {
			m.selectedAgent = 0
		}
		m.syncAgentInputsFromSelection()
		selectedID := m.agents[m.selectedAgent].ID
		cmds = append(cmds, m.loadSessionsCmd(selectedID), m.loadAgentWorkspacesCmd(selectedID))
		m.status = fmt.Sprintf("Loaded %d agents", len(m.agents))
	case sessionsLoadedMsg:
		m.sessions = msg.sessions
		if len(m.sessions) == 0 {
			m.selectedSession = 0
			m.messages = nil
			m.messageOutput.SetContent("")
			m.status = "No sessions for selected agent"
			break
		}
		if m.selectedSession >= len(m.sessions) {
			m.selectedSession = 0
		}
		cmds = append(cmds, m.loadHistoryCmd(m.selectedSessionID(), false))
		m.status = fmt.Sprintf("Loaded %d sessions", len(m.sessions))
	case historyLoadedMsg:
		if msg.session != m.selectedSessionID() {
			break
		}
		m.rebuildMessages(msg.history)
		status := ""
		if msg.history.RunStatus != nil {
			status = *msg.history.RunStatus
		}
		if status == "pending" || status == "waiting" || status == "running" {
			m.polling = true
			cmds = append(cmds, m.pollCmd())
			m.status = "Run in progress..."
		} else {
			m.polling = false
			m.status = "Run complete"
		}
	case sendDoneMsg:
		m.chatInput.SetValue("")
		m.status = "Message sent"
		cmds = append(cmds, m.loadSessionsCmd(m.selectedAgentID()), m.loadHistoryCmd(msg.sessionID, true))
		m.polling = true
	case pollTickMsg:
		if m.polling {
			sid := m.selectedSessionID()
			if sid != "" {
				cmds = append(cmds, m.loadHistoryCmd(sid, true))
			}
		}
	case workspacesLoadedMsg:
		m.workspaces = msg.workspaces
		if m.selectedWorkspace >= len(m.workspaces) {
			m.selectedWorkspace = 0
		}
		m.syncWorkspaceInputsFromSelection()
		m.status = fmt.Sprintf("Loaded %d workspaces", len(m.workspaces))
	case agentWorkspacesLoadedMsg:
		m.agentWorkspaces = msg.entries
		if m.selectedAssignment >= len(m.agentWorkspaces) {
			m.selectedAssignment = 0
		}
	case approvalsLoadedMsg:
		m.approvals = msg.approvals
		if m.selectedApproval >= len(m.approvals) {
			m.selectedApproval = 0
		}
		m.status = fmt.Sprintf("Loaded %d pending approvals", len(m.approvals))
	case composeResultMsg:
		if msg.output == "" {
			msg.output = "Command completed with no output"
		}
		m.composeOutput.SetContent(msg.output)
		m.composeOutput.GotoBottom()
		m.status = "Compose command complete"
	case agentSavedMsg:
		m.status = fmt.Sprintf("Saved agent %s", msg.agent.Name)
		m.lastSavedAgent = msg.agent.ID
		m.creatingAgent = false
		cmds = append(cmds, m.loadAgentsCmd())
	case workspaceSavedMsg:
		m.status = fmt.Sprintf("Saved workspace %s", msg.workspace.Name)
		cmds = append(cmds, m.loadWorkspacesCmd())
	case tea.MouseMsg:
		if msg.Y == 0 && msg.Action == tea.MouseActionPress && msg.Button == tea.MouseButtonLeft {
			m.activeTab = tabAtX(msg.X)
			m.applyFocus()
			return m, nil
		}
		if msg.Action == tea.MouseActionPress && msg.Button == tea.MouseButtonLeft {
			switch m.activeTab {
			case tabChat:
				m.handleChatMouseClick(msg.Y)
			case tabAgents:
				cmds = append(cmds, m.handleAgentsMouseClick(msg.X, msg.Y)...)
			case tabWorkspaces:
				cmds = append(cmds, m.handleWorkspacesMouseClick(msg.X, msg.Y)...)
			}
		}

		switch msg.Button {
		case tea.MouseButtonWheelUp, tea.MouseButtonWheelDown:
			var cmd tea.Cmd
			switch m.activeTab {
			case tabChat:
				m.messageOutput, cmd = m.messageOutput.Update(msg)
				cmds = append(cmds, cmd)
			case tabCompose:
				m.composeOutput, cmd = m.composeOutput.Update(msg)
				cmds = append(cmds, cmd)
			}
		}
	case tea.KeyMsg:
		switch msg.String() {
		case "ctrl+c", "ctrl+q":
			return m, tea.Quit
		case "tab":
			m.activeTab = (m.activeTab + 1) % 4
			m.applyFocus()
			return m, nil
		case "shift+tab":
			m.activeTab--
			if m.activeTab < 0 {
				m.activeTab = tabCompose
			}
			m.applyFocus()
			return m, nil
		}

		switch m.activeTab {
		case tabChat:
			switch msg.String() {
			case "ctrl+o":
				m.chatFocus = (m.chatFocus + 1) % 2
				m.applyFocus()
			case "ctrl+a":
				if len(m.agents) > 0 {
					m.selectedAgent = (m.selectedAgent + 1) % len(m.agents)
					m.syncAgentInputsFromSelection()
					selected := m.agents[m.selectedAgent]
					cmds = append(cmds, m.loadSessionsCmd(selected.ID), m.loadAgentWorkspacesCmd(selected.ID))
				}
			case "ctrl+s":
				if len(m.sessions) > 0 {
					m.selectedSession = (m.selectedSession + 1) % len(m.sessions)
					cmds = append(cmds, m.loadHistoryCmd(m.selectedSessionID(), false))
				}
			case "ctrl+n":
				if id := m.selectedAgentID(); id != 0 {
					text := strings.TrimSpace(m.chatInput.Value())
					if text == "" {
						cmds = append(cmds, func() tea.Msg {
							created, err := m.client.CreateSession(context.Background(), id)
							if err != nil {
								return errMsg{err: err}
							}
							return sendDoneMsg{sessionID: created.SessionID}
						})
					}
				}
			case "ctrl+r":
				if id := m.selectedAgentID(); id != 0 {
					cmds = append(cmds, m.loadSessionsCmd(id))
				}
			case "enter":
				text := strings.TrimSpace(m.chatInput.Value())
				if text != "" {
					sid := m.selectedSessionID()
					m.messages = append(m.messages, chatMessage{role: "user", text: text})
					m.messageOutput.SetContent(m.messageOutput.View() + "\n\n[user] " + text)
					cmds = append(cmds, m.sendChatCmd(m.selectedAgentID(), sid, text))
				}
			case "ctrl+p":
				sid := strings.TrimSpace(m.approvalsSession.Value())
				if sid == "" {
					sid = m.selectedSessionID()
				}
				if sid != "" {
					cmds = append(cmds, m.loadApprovalsCmd(sid))
				}
			case "ctrl+y":
				if len(m.approvals) > 0 {
					sid := m.selectedSessionID()
					if sid == "" {
						sid = strings.TrimSpace(m.approvalsSession.Value())
					}
					if sid != "" {
						token := m.approvals[m.selectedApproval].ApprovalToken
						cmds = append(cmds, func() tea.Msg {
							err := m.client.ResolveApproval(context.Background(), sid, token, true)
							if err != nil {
								return errMsg{err: err}
							}
							return approvalsLoadedMsg{approvals: []api.ApprovalEvent{}}
						}, m.loadApprovalsCmd(sid))
					}
				}
			case "ctrl+d":
				if len(m.approvals) > 0 {
					sid := m.selectedSessionID()
					if sid == "" {
						sid = strings.TrimSpace(m.approvalsSession.Value())
					}
					if sid != "" {
						token := m.approvals[m.selectedApproval].ApprovalToken
						cmds = append(cmds, func() tea.Msg {
							err := m.client.ResolveApproval(context.Background(), sid, token, false)
							if err != nil {
								return errMsg{err: err}
							}
							return approvalsLoadedMsg{approvals: []api.ApprovalEvent{}}
						}, m.loadApprovalsCmd(sid))
					}
				}
			}
			var cmd tea.Cmd
			m.chatInput, cmd = m.chatInput.Update(msg)
			cmds = append(cmds, cmd)
			m.messageOutput, cmd = m.messageOutput.Update(msg)
			cmds = append(cmds, cmd)
			m.approvalsSession, cmd = m.approvalsSession.Update(msg)
			cmds = append(cmds, cmd)
		case tabAgents:
			switch msg.String() {
			case "ctrl+o":
				m.agentFieldFocus = (m.agentFieldFocus + 1) % 3
				m.applyFocus()
			case "j", "down":
				if m.selectedAgent < len(m.agents)-1 {
					m.selectedAgent++
					m.syncAgentInputsFromSelection()
				}
			case "k", "up":
				if m.selectedAgent > 0 {
					m.selectedAgent--
					m.syncAgentInputsFromSelection()
				}
			case "ctrl+r":
				cmds = append(cmds, m.loadAgentsCmd())
			case "ctrl+s":
				temp, err := strconv.ParseFloat(strings.TrimSpace(m.agentTempInput.Value()), 64)
				if err != nil {
					m.err = "invalid temperature"
					break
				}
				id := int64(0)
				if !m.creatingAgent && len(m.agents) > 0 {
					id = m.agents[m.selectedAgent].ID
				}
				create := id == 0
				cmds = append(cmds, m.saveAgentCmd(id, create, strings.TrimSpace(m.agentNameInput.Value()), strings.TrimSpace(m.agentModelInput.Value()), temp))
			case "ctrl+n":
				m.agentNameInput.SetValue("New Agent")
				m.agentModelInput.SetValue("openai/gpt-oss-20b")
				m.agentTempInput.SetValue("0.1")
				m.creatingAgent = true
				m.status = "New agent draft ready; edit fields and press ctrl+s to save"
			case "e":
				m.syncAgentInputsFromSelection()
				m.status = "Loaded selected agent into editor"
			}
			var cmd tea.Cmd
			m.agentNameInput, cmd = m.agentNameInput.Update(msg)
			cmds = append(cmds, cmd)
			m.agentModelInput, cmd = m.agentModelInput.Update(msg)
			cmds = append(cmds, cmd)
			m.agentTempInput, cmd = m.agentTempInput.Update(msg)
			cmds = append(cmds, cmd)
		case tabWorkspaces:
			switch msg.String() {
			case "ctrl+o":
				m.workspaceFieldFocus = (m.workspaceFieldFocus + 1) % 2
				m.applyFocus()
			case "j", "down":
				if m.selectedWorkspace < len(m.workspaces)-1 {
					m.selectedWorkspace++
					m.syncWorkspaceInputsFromSelection()
				}
			case "k", "up":
				if m.selectedWorkspace > 0 {
					m.selectedWorkspace--
					m.syncWorkspaceInputsFromSelection()
				}
			case "ctrl+r":
				cmds = append(cmds, m.loadWorkspacesCmd())
			case "ctrl+s":
				name := strings.TrimSpace(m.workspaceName.Value())
				if name != "" {
					cmds = append(cmds, m.saveWorkspaceCmd(name, strings.TrimSpace(m.workspaceRootPath.Value())))
				}
			case "ctrl+a":
				if len(m.workspaces) > 0 && len(m.agents) > 0 {
					agentID := m.agents[m.selectedAgent].ID
					workspaceID := m.workspaces[m.selectedWorkspace].ID
					cmds = append(cmds, func() tea.Msg {
						err := m.client.AssignWorkspace(context.Background(), agentID, workspaceID, "confirm_writes_and_exec", len(m.agentWorkspaces) == 0)
						if err != nil {
							return errMsg{err: err}
						}
						return agentWorkspacesLoadedMsg{}
					}, m.loadAgentWorkspacesCmd(agentID))
				}
			case "ctrl+u":
				if len(m.agentWorkspaces) > 0 && len(m.agents) > 0 {
					agentID := m.agents[m.selectedAgent].ID
					idx := m.selectedAssignment
					if idx < 0 || idx >= len(m.agentWorkspaces) {
						idx = 0
					}
					workspaceID := m.agentWorkspaces[idx].Workspace.ID
					cmds = append(cmds, func() tea.Msg {
						err := m.client.UnassignWorkspace(context.Background(), agentID, workspaceID)
						if err != nil {
							return errMsg{err: err}
						}
						return agentWorkspacesLoadedMsg{}
					}, m.loadAgentWorkspacesCmd(agentID))
				}
			case "ctrl+d":
				if len(m.workspaces) > 0 {
					workspaceID := m.workspaces[m.selectedWorkspace].ID
					cmds = append(cmds, func() tea.Msg {
						err := m.client.DeleteWorkspace(context.Background(), workspaceID)
						if err != nil {
							return errMsg{err: err}
						}
						return workspacesLoadedMsg{}
					}, m.loadWorkspacesCmd())
				}
			case "ctrl+n":
				m.workspaceName.SetValue("")
				m.workspaceRootPath.SetValue("")
				m.status = "New workspace draft ready; fill fields and press ctrl+s to save"
			case "e":
				m.syncWorkspaceInputsFromSelection()
				m.status = "Loaded selected workspace into editor"
			case "left":
				if m.selectedAssignment > 0 {
					m.selectedAssignment--
				}
			case "right":
				if m.selectedAssignment < len(m.agentWorkspaces)-1 {
					m.selectedAssignment++
				}
			}
			var cmd tea.Cmd
			m.workspaceName, cmd = m.workspaceName.Update(msg)
			cmds = append(cmds, cmd)
			m.workspaceRootPath, cmd = m.workspaceRootPath.Update(msg)
			cmds = append(cmds, cmd)
		case tabCompose:
			switch msg.String() {
			case "u":
				cmds = append(cmds, m.composeCmd("start", nil, false, false))
				m.status = "Starting compose services"
			case "b":
				cmds = append(cmds, m.composeCmd("start", nil, true, false))
				m.status = "Starting compose services with build"
			case "o":
				cmds = append(cmds, m.composeCmd("stop", nil, false, false))
				m.status = "Stopping compose services"
			case "v":
				cmds = append(cmds, m.composeCmd("stop", nil, false, true))
				m.status = "Stopping compose services and removing volumes"
			case "r":
				cmds = append(cmds, m.composeCmd("status", nil, false, false))
				m.status = "Reading compose status"
			}
			var cmd tea.Cmd
			m.composeOutput, cmd = m.composeOutput.Update(msg)
			cmds = append(cmds, cmd)
		}
	}

	return m, tea.Batch(cmds...)
}

func (m model) View() string {
	tabNames := []string{"Chat", "Agents", "Workspaces", "Compose"}
	parts := make([]string, 0, len(tabNames))
	for i, name := range tabNames {
		style := inactiveTabStyle
		if int(m.activeTab) == i {
			style = activeTabStyle
		}
		parts = append(parts, style.Render(name))
	}

	head := titleStyle.Render("SharpClaw TUI") + "  " + strings.Join(parts, "  ")
	body := ""
	switch m.activeTab {
	case tabChat:
		body = m.renderChat()
	case tabAgents:
		body = m.renderAgents()
	case tabWorkspaces:
		body = m.renderWorkspaces()
	case tabCompose:
		body = m.renderCompose()
	}

	status := mutedStyle.Render(m.status)
	if m.err != "" {
		status = errorStyle.Render(m.err)
	}
	help := mutedStyle.Render("tab/shift+tab: switch panel | ctrl+q: quit")
	return lipgloss.JoinVertical(lipgloss.Left, head, body, status, help)
}

func (m model) renderChat() string {
	agentName := "none"
	if len(m.agents) > 0 {
		idx := m.selectedAgent
		if idx < 0 {
			idx = 0
		}
		if idx >= len(m.agents) {
			idx = len(m.agents) - 1
		}
		agentName = fmt.Sprintf("%d:%s", m.agents[idx].ID, m.agents[idx].Name)
	}
	sessionID := m.selectedSessionID()
	if sessionID == "" {
		sessionID = "none"
	}

	metaTotalHeight, messagesTotalHeight, approvalsTotalHeight, inputTotalHeight := m.chatSectionHeights()
	messagesInnerHeight := max(1, messagesTotalHeight-2)

	msgViewport := m.messageOutput
	msgViewport.Height = messagesInnerHeight
	msgViewport.Width = max(30, m.width-6)

	meta := panelStyle.Width(max(20, m.width-2)).MaxHeight(metaTotalHeight).Render(
		fmt.Sprintf("Agent: %s | Session: %s | Sessions: %d", agentName, sessionID, len(m.sessions)) + "\n" +
			"ctrl+a:next-agent ctrl+s:next-session ctrl+n:new-session ctrl+r:refresh enter:send",
	)
	messages := panelStyle.Width(max(20, m.width-2)).MaxHeight(messagesTotalHeight).Render(msgViewport.View())
	approvals := panelStyle.Width(max(20, m.width-2)).MaxHeight(approvalsTotalHeight).Render(m.renderApprovals())
	input := panelStyle.Width(max(20, m.width-2)).MaxHeight(inputTotalHeight).Render("Message\n" + m.chatInput.View() + "\nApprovals session (ctrl+o focus): " + m.approvalsSession.View() + "\nctrl+p:load approvals ctrl+y:approve ctrl+d:reject")
	return lipgloss.JoinVertical(lipgloss.Left, meta, messages, approvals, input)
}

func (m model) renderApprovals() string {
	if len(m.approvals) == 0 {
		return "No pending approvals"
	}
	rows := make([]string, 0, len(m.approvals))
	for i, a := range m.approvals {
		prefix := "  "
		if i == m.selectedApproval {
			prefix = "> "
		}
		rows = append(rows, fmt.Sprintf("%s[%s] %s (%s)", prefix, a.RiskLevel, a.ActionType, a.ApprovalToken))
	}
	return strings.Join(rows, "\n")
}

func (m model) renderAgents() string {
	list := make([]string, 0, len(m.agents))
	if len(m.agents) == 0 {
		list = append(list, "No agents")
	}
	for i, agent := range m.agents {
		prefix := "  "
		if i == m.selectedAgent {
			prefix = "> "
		}
		list = append(list, fmt.Sprintf("%s%d %s (%s, temp %.2f)", prefix, agent.ID, agent.Name, agent.LlmModel, agent.Temperature))
	}

	left := panelStyle.Width(max(30, m.width/2-2)).Height(max(8, m.height-10)).Render(strings.Join(list, "\n"))
	form := panelStyle.Width(max(30, m.width/2-2)).Height(max(8, m.height-10)).Render(
		"Agent Editor\n" +
			"Name: " + m.agentNameInput.View() + "\n" +
			"Model: " + m.agentModelInput.View() + "\n" +
			"Temp: " + m.agentTempInput.View() + "\n\n" +
			"ctrl+o:next field | ctrl+n:new draft | e:load selected | ctrl+s:save | ctrl+r:reload | up/down:select",
	)

	return lipgloss.JoinHorizontal(lipgloss.Top, left, form)
}

func (m model) renderWorkspaces() string {
	workspaces := make([]string, 0, len(m.workspaces))
	if len(m.workspaces) == 0 {
		workspaces = append(workspaces, "No workspaces")
	}
	for i, ws := range m.workspaces {
		prefix := "  "
		if i == m.selectedWorkspace {
			prefix = "> "
		}
		workspaces = append(workspaces, fmt.Sprintf("%s%d %s (%s)", prefix, ws.ID, ws.Name, ws.RootPath))
	}

	assigned := make([]string, 0, len(m.agentWorkspaces))
	if len(m.agentWorkspaces) == 0 {
		assigned = append(assigned, "No assigned workspaces for selected agent")
	}
	for i, entry := range m.agentWorkspaces {
		prefix := "  "
		if i == m.selectedAssignment {
			prefix = "> "
		}
		assigned = append(assigned, fmt.Sprintf("%s%s [%s] default=%t", prefix, entry.Workspace.Name, entry.Assignment.PolicyMode, entry.Assignment.IsDefault))
	}

	left := panelStyle.Width(max(30, m.width/2-2)).Height(max(8, m.height-10)).Render(strings.Join(workspaces, "\n"))
	right := panelStyle.Width(max(30, m.width/2-2)).Height(max(8, m.height-10)).Render(
		"Workspace Editor\n" +
			"Name: " + m.workspaceName.View() + "\n" +
			"Root: " + m.workspaceRootPath.View() + "\n\n" +
			"Assigned To Selected Agent\n" + strings.Join(assigned, "\n") + "\n\n" +
			"ctrl+o:next field | ctrl+n:new draft | e:load selected | ctrl+s:save | ctrl+a:assign | ctrl+u:unassign | ctrl+d:delete | ctrl+r:reload",
	)

	return lipgloss.JoinHorizontal(lipgloss.Top, left, right)
}

func (m model) renderCompose() string {
	header := panelStyle.Width(max(20, m.width-2)).Render(
		fmt.Sprintf("compose command: %s | file: %s", m.cfg.ComposeCommand, m.cfg.ComposeFile) + "\n" +
			"u:up b:up --build o:down v:down -v r:ps",
	)
	content := panelStyle.Width(max(20, m.width-2)).Height(max(8, m.height-12)).Render(m.composeOutput.View())
	return lipgloss.JoinVertical(lipgloss.Left, header, content)
}

func Run(cfg config.Config, runner compose.Runner) error {
	p := tea.NewProgram(New(cfg, runner), tea.WithAltScreen(), tea.WithMouseCellMotion())
	_, err := p.Run()
	return err
}

func max(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
