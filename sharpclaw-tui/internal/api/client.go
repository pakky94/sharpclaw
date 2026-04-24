package api

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

type Client struct {
	baseURL    string
	httpClient *http.Client
}

func NewClient(baseURL string) *Client {
	return &Client{
		baseURL: strings.TrimRight(baseURL, "/"),
		httpClient: &http.Client{
			Timeout: 60 * time.Second,
		},
	}
}

type AgentConfig struct {
	ID          int64   `json:"id"`
	Name        string  `json:"name"`
	LlmModel    string  `json:"llmModel"`
	Temperature float64 `json:"temperature"`
	CreatedAt   string  `json:"createdAt"`
	UpdatedAt   string  `json:"updatedAt"`
}

type SessionSummary struct {
	SessionID        string  `json:"sessionId"`
	AgentID          int64   `json:"agentId"`
	Name             *string `json:"name"`
	VisibleInSidebar bool    `json:"visibleInSidebar"`
	ParentSessionID  *string `json:"parentSessionId"`
	CreatedAt        string  `json:"createdAt"`
	UpdatedAt        string  `json:"updatedAt"`
	MessagesCount    int64   `json:"messagesCount"`
}

type SessionHistoryResponse struct {
	SessionID       string                  `json:"sessionId"`
	ParentSessionID *string                 `json:"parentSessionId"`
	RunStatus       *string                 `json:"runStatus"`
	LatestSequence  int64                   `json:"latestSequenceId"`
	Messages        []SessionHistoryMessage `json:"messages"`
}

type SessionHistoryMessage struct {
	Role      string                  `json:"role"`
	Text      *string                 `json:"text"`
	Contents  []SessionMessageContent `json:"contents"`
	Author    *string                 `json:"authorName"`
	MessageID int64                   `json:"messageId"`
	RunStatus *string                 `json:"runStatus"`
}

type SessionMessageContent struct {
	Type     string          `json:"type"`
	Text     *string         `json:"text"`
	CallID   *string         `json:"callId"`
	ToolName *string         `json:"toolName"`
	Args     json.RawMessage `json:"arguments"`
	Result   json.RawMessage `json:"result"`
}

type WorkspaceConfig struct {
	ID                int64    `json:"id"`
	Name              string   `json:"name"`
	RootPath          string   `json:"rootPath"`
	AllowlistPatterns []string `json:"allowlistPatterns"`
	DenylistPatterns  []string `json:"denylistPatterns"`
	CreatedAt         string   `json:"createdAt"`
	UpdatedAt         string   `json:"updatedAt"`
}

type WorkspaceAssignment struct {
	ID         int64  `json:"id"`
	PolicyMode string `json:"policyMode"`
	IsDefault  bool   `json:"isDefault"`
}

type AgentWorkspaceEntry struct {
	Workspace  WorkspaceConfig     `json:"workspace"`
	Assignment WorkspaceAssignment `json:"assignment"`
}

type AgentFragmentSummary struct {
	Name        string `json:"name"`
	Path        string `json:"path"`
	HasChildren bool   `json:"hasChildren"`
}

type AgentFile struct {
	Path    string `json:"path"`
	Content string `json:"content"`
}

type ApprovalEvent struct {
	ApprovalToken string  `json:"approvalToken"`
	ActionType    string  `json:"actionType"`
	TargetPath    *string `json:"targetPath"`
	Command       *string `json:"commandPreview"`
	RiskLevel     string  `json:"riskLevel"`
}

type CreateSessionResponse struct {
	SessionID string `json:"sessionId"`
}

type SendMessageResponse struct {
	LatestSequenceID int64  `json:"latestSequenceId"`
	SessionID        string `json:"sessionId"`
	Status           string `json:"status"`
}

type StreamEvent struct {
	Type      string          `json:"type"`
	MessageID int64           `json:"messageId"`
	SessionID string          `json:"sessionId"`
	Sequence  int64           `json:"sequence"`
	Text      *string         `json:"text"`
	Status    *string         `json:"status"`
	Data      json.RawMessage `json:"data"`
}

func (c *Client) doJSON(ctx context.Context, method, path string, body any, out any) error {
	var bodyReader io.Reader
	if body != nil {
		data, err := json.Marshal(body)
		if err != nil {
			return fmt.Errorf("marshal request body: %w", err)
		}
		bodyReader = bytes.NewReader(data)
	}

	req, err := http.NewRequestWithContext(ctx, method, c.baseURL+path, bodyReader)
	if err != nil {
		return fmt.Errorf("create request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	res, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("perform request: %w", err)
	}
	defer res.Body.Close()

	if res.StatusCode >= 400 {
		payload, _ := io.ReadAll(res.Body)
		return fmt.Errorf("request failed (%d): %s", res.StatusCode, strings.TrimSpace(string(payload)))
	}

	if out == nil {
		_, _ = io.Copy(io.Discard, res.Body)
		return nil
	}

	if err := json.NewDecoder(res.Body).Decode(out); err != nil {
		return fmt.Errorf("decode response: %w", err)
	}
	return nil
}

func (c *Client) ListAgents(ctx context.Context) ([]AgentConfig, error) {
	var res struct {
		Agents []AgentConfig `json:"agents"`
	}
	if err := c.doJSON(ctx, http.MethodGet, "/agents", nil, &res); err != nil {
		return nil, err
	}
	return res.Agents, nil
}

func (c *Client) CreateAgent(ctx context.Context, name, model string, temperature float64) (AgentConfig, error) {
	var agent AgentConfig
	err := c.doJSON(ctx, http.MethodPost, "/agents", map[string]any{
		"name":        name,
		"llmModel":    model,
		"temperature": temperature,
	}, &agent)
	return agent, err
}

func (c *Client) UpdateAgent(ctx context.Context, id int64, name, model string, temperature float64) (AgentConfig, error) {
	var agent AgentConfig
	err := c.doJSON(ctx, http.MethodPut, fmt.Sprintf("/agents/%d", id), map[string]any{
		"name":        name,
		"llmModel":    model,
		"temperature": temperature,
	}, &agent)
	return agent, err
}

func (c *Client) ListAgentSessions(ctx context.Context, agentID int64) ([]SessionSummary, error) {
	var res struct {
		Sessions []SessionSummary `json:"sessions"`
	}
	if err := c.doJSON(ctx, http.MethodGet, fmt.Sprintf("/agents/%d/sessions", agentID), nil, &res); err != nil {
		return nil, err
	}
	return res.Sessions, nil
}

func (c *Client) CreateSession(ctx context.Context, agentID int64, name string) (CreateSessionResponse, error) {
	var out CreateSessionResponse
	payload := map[string]any{"agentId": agentID}
	if strings.TrimSpace(name) != "" {
		payload["name"] = name
	}
	err := c.doJSON(ctx, http.MethodPost, "/sessions", payload, &out)
	return out, err
}

func (c *Client) RenameSession(ctx context.Context, sessionID, name string) error {
	return c.doJSON(ctx, http.MethodPatch, fmt.Sprintf("/sessions/%s", sessionID), map[string]any{
		"name": name,
	}, nil)
}

func (c *Client) SendMessage(ctx context.Context, sessionID, message string) (SendMessageResponse, error) {
	var out SendMessageResponse
	err := c.doJSON(ctx, http.MethodPost, fmt.Sprintf("/sessions/%s/messages", sessionID), map[string]any{"message": message}, &out)
	return out, err
}

func (c *Client) ResumeSession(ctx context.Context, sessionID string) error {
	return c.doJSON(ctx, http.MethodPost, fmt.Sprintf("/sessions/%s/resume", sessionID), nil, nil)
}

func (c *Client) SessionHistory(ctx context.Context, sessionID string) (SessionHistoryResponse, error) {
	var out SessionHistoryResponse
	err := c.doJSON(ctx, http.MethodGet, fmt.Sprintf("/sessions/%s/history", sessionID), nil, &out)
	return out, err
}

func (c *Client) ListWorkspaces(ctx context.Context) ([]WorkspaceConfig, error) {
	var out struct {
		Workspaces []WorkspaceConfig `json:"workspaces"`
	}
	if err := c.doJSON(ctx, http.MethodGet, "/workspaces", nil, &out); err != nil {
		return nil, err
	}
	return out.Workspaces, nil
}

func (c *Client) UpsertWorkspace(ctx context.Context, name, root string, allowlist, denylist []string) (WorkspaceConfig, error) {
	var out WorkspaceConfig
	err := c.doJSON(ctx, http.MethodPut, "/workspaces", map[string]any{
		"name":              name,
		"rootPath":          root,
		"allowlistPatterns": allowlist,
		"denylistPatterns":  denylist,
	}, &out)
	return out, err
}

func (c *Client) DeleteWorkspace(ctx context.Context, workspaceID int64) error {
	return c.doJSON(ctx, http.MethodDelete, fmt.Sprintf("/workspaces/%d", workspaceID), nil, nil)
}

func (c *Client) AgentWorkspaces(ctx context.Context, agentID int64) ([]AgentWorkspaceEntry, error) {
	var out struct {
		Workspaces []AgentWorkspaceEntry `json:"workspaces"`
	}
	if err := c.doJSON(ctx, http.MethodGet, fmt.Sprintf("/agents/%d/workspaces", agentID), nil, &out); err != nil {
		return nil, err
	}
	return out.Workspaces, nil
}

func (c *Client) AssignWorkspace(ctx context.Context, agentID, workspaceID int64, policyMode string, isDefault bool) error {
	return c.doJSON(ctx, http.MethodPut, fmt.Sprintf("/agents/%d/workspaces/%d", agentID, workspaceID), map[string]any{
		"policyMode": policyMode,
		"isDefault":  isDefault,
	}, nil)
}

func (c *Client) UnassignWorkspace(ctx context.Context, agentID, workspaceID int64) error {
	return c.doJSON(ctx, http.MethodDelete, fmt.Sprintf("/agents/%d/workspaces/%d", agentID, workspaceID), nil, nil)
}

func (c *Client) PendingApprovals(ctx context.Context, sessionID string) ([]ApprovalEvent, error) {
	var out struct {
		Approvals []ApprovalEvent `json:"approvals"`
	}
	if err := c.doJSON(ctx, http.MethodGet, fmt.Sprintf("/sessions/%s/approvals/pending", sessionID), nil, &out); err != nil {
		return nil, err
	}
	return out.Approvals, nil
}

func (c *Client) ResolveApproval(ctx context.Context, sessionID, token string, approved bool) error {
	action := "reject"
	if approved {
		action = "approve"
	}
	return c.doJSON(ctx, http.MethodPost, fmt.Sprintf("/sessions/%s/approvals/%s/%s", sessionID, token, action), nil, nil)
}

func (c *Client) ListFragments(ctx context.Context, agentID int64, parentPath string) ([]AgentFragmentSummary, error) {
	path := fmt.Sprintf("/agents/%d/fragments", agentID)
	if strings.TrimSpace(parentPath) != "" {
		path += "?parentPath=" + url.QueryEscape(parentPath)
	}

	var out struct {
		Fragments []AgentFragmentSummary `json:"fragments"`
	}
	if err := c.doJSON(ctx, http.MethodGet, path, nil, &out); err != nil {
		return nil, err
	}
	return out.Fragments, nil
}

func (c *Client) ReadFragment(ctx context.Context, agentID int64, path string) (AgentFile, error) {
	var out AgentFile
	err := c.doJSON(ctx, http.MethodGet, fmt.Sprintf("/agents/%d/fragments/file?path=%s", agentID, url.QueryEscape(path)), nil, &out)
	return out, err
}

func (c *Client) UpsertFragment(ctx context.Context, agentID int64, path, content string) error {
	return c.doJSON(ctx, http.MethodPut, fmt.Sprintf("/agents/%d/fragments/file", agentID), map[string]any{
		"path":    path,
		"content": content,
	}, nil)
}

func (c *Client) DeleteFragment(ctx context.Context, agentID int64, path string) error {
	return c.doJSON(ctx, http.MethodDelete, fmt.Sprintf("/agents/%d/fragments/file?path=%s", agentID, url.QueryEscape(path)), nil, nil)
}

func (c *Client) StreamSession(ctx context.Context, sessionID string, latestSequence int64, onEvent func(StreamEvent) error) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, fmt.Sprintf("%s/sessions/%s/messages/%d/stream", c.baseURL, sessionID, latestSequence), nil)
	if err != nil {
		return fmt.Errorf("create stream request: %w", err)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return fmt.Errorf("open stream: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 400 {
		blob, _ := io.ReadAll(resp.Body)
		return fmt.Errorf("stream request failed (%d): %s", resp.StatusCode, strings.TrimSpace(string(blob)))
	}

	scanner := bufio.NewScanner(resp.Body)
	scanner.Buffer(make([]byte, 0, 1024), 1024*1024)

	var currentEvent string
	for scanner.Scan() {
		line := scanner.Text()
		if strings.HasPrefix(line, "event:") {
			currentEvent = strings.TrimSpace(strings.TrimPrefix(line, "event:"))
			continue
		}
		if strings.HasPrefix(line, "data:") {
			raw := strings.TrimSpace(strings.TrimPrefix(line, "data:"))
			if raw == "" {
				continue
			}
			var event StreamEvent
			if err := json.Unmarshal([]byte(raw), &event); err != nil {
				return fmt.Errorf("decode stream event: %w", err)
			}
			if event.Type == "" {
				event.Type = currentEvent
			}
			if err := onEvent(event); err != nil {
				return err
			}
		}
	}

	if err := scanner.Err(); err != nil && !strings.Contains(err.Error(), "closed") {
		return fmt.Errorf("read stream: %w", err)
	}

	return nil
}
