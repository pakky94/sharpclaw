using System.Collections.Concurrent;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Agents;

public class SessionStore(
    ChatRepository chatRepository,
    WorkspaceRepository workspaceRepository,
    AgentsRepository agentsRepository
)
{
    private readonly ConcurrentDictionary<Guid, AgentSessionState> _sessions = new();
    private readonly ConcurrentDictionary<Guid, byte> _executingRuns = new();
    private readonly SemaphoreSlim _sessionsMutex = new(1, 1);

    public async Task<AgentSessionState> GetOrLoadSession(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
            return existing;

        await _sessionsMutex.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(sessionId, out existing))
                return existing;

            var persistedSession = await chatRepository.GetSession(sessionId)
                                   ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");
            var agentConfig = await agentsRepository.GetAgent(persistedSession.AgentId)
                              ?? throw new KeyNotFoundException($"Agent {persistedSession.AgentId} was not found.");

            var defaultWs = await workspaceRepository.ResolveDefaultWorkspace(persistedSession.AgentId);
            var activeWsRows = await workspaceRepository.GetActiveWorkspacesForSession(sessionId);
            var activeWsNames = new HashSet<string>(activeWsRows.Select(r => r.WorkspaceName));
            var sessionDependencies = await chatRepository.GetSessionTaskLinks(sessionId);

            if (activeWsNames.Count == 0 && defaultWs is not null)
            {
                activeWsNames.Add(defaultWs.Name);
            }

            var context = new AgentExecutionContext
            {
                SessionId = sessionId,
                AgentId = persistedSession.AgentId,
                LlmModel = agentConfig.LlmModel,
                Temperature = agentConfig.Temperature,
                SoftCompactThreshold = agentConfig.SoftCompactThreshold,
                HardCompactThreshold = agentConfig.HardCompactThreshold,
                Messages = [..await chatRepository.LoadActiveConversation(sessionId)],
                Workspace = defaultWs,
                ActiveWorkspaceNames = activeWsNames,
            };

            var loadedSession = new AgentSessionState(
                sessionId,
                context,
                parentSessionId: persistedSession.ParentSessionId,
                createdAt: new DateTimeOffset(DateTime.SpecifyKind(persistedSession.CreatedAt, DateTimeKind.Utc)),
                sessionDependencies: sessionDependencies
                    .Where(sd => !sd.Completed)
                    .Select(sd => new SessionDependency(sd.ChildSessionId, sd.CallId))
                    .ToList()
            );
            loadedSession.Run.SetStatus(persistedSession.Status.ToLowerInvariant() switch
            {
                "pending" => AgentRunStatus.Pending,
                "running" => AgentRunStatus.Running,
                "waiting" => AgentRunStatus.Waiting,
                "failed" => AgentRunStatus.Failed,
                _ => AgentRunStatus.Completed,
            });
            _sessions[sessionId] = loadedSession;
            return loadedSession;
        }
        finally
        {
            _sessionsMutex.Release();
        }
    }

    public AgentRunState? GetActiveRunForSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        return session.Run is { Status: AgentRunStatus.Pending or AgentRunStatus.Running }
            ? session.Run
            : null;
    }

    public AgentRunState? GetLiveRunForSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        return session.Run is { Status: AgentRunStatus.Pending or AgentRunStatus.Running or AgentRunStatus.Waiting }
            ? session.Run
            : null;
    }

    public bool TryUpdateSessionActiveWorkspaces(Guid sessionId, string[] workspaceNames)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        session.Context.ActiveWorkspaceNames = new HashSet<string>(workspaceNames, StringComparer.OrdinalIgnoreCase);
        return true;
    }

    public Task Add(AgentSessionState session)
    {
        _sessions.TryAdd(session.SessionId, session);
        return Task.CompletedTask;
    }

    public bool TryMarkRunExecuting(Guid sessionId)
    {
        return _executingRuns.TryAdd(sessionId, 0);
    }

    public void ClearRunExecuting(Guid sessionId)
    {
        _executingRuns.TryRemove(sessionId, out _);
    }

    public bool IsRunExecuting(Guid sessionId)
    {
        return _executingRuns.ContainsKey(sessionId);
    }

    /// <summary>
    /// Refreshes the agent-level configuration (LlmModel, Temperature, compaction thresholds)
    /// for all in-memory sessions belonging to the given agent. This ensures that changes made
    /// via the agent update endpoint take effect immediately without requiring a restart.
    /// </summary>
    public async Task RefreshAgentConfigForSessions(long agentId)
    {
        var agentConfig = await agentsRepository.GetAgent(agentId);
        if (agentConfig is null)
            return;

        foreach (var kvp in _sessions)
        {
            var session = kvp.Value;
            if (session.Context.AgentId != agentId)
                continue;

            session.Context.LlmModel = agentConfig.LlmModel;
            session.Context.Temperature = agentConfig.Temperature;
            session.Context.SoftCompactThreshold = agentConfig.SoftCompactThreshold;
            session.Context.HardCompactThreshold = agentConfig.HardCompactThreshold;
        }
    }
}
