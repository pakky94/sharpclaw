using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Memory.Lcm;
using SharpClaw.API.Agents.Tools.Fragments;
using SharpClaw.API.Agents.Tools.Lcm;
using SharpClaw.API.Agents.Tools.Tasks;
using SharpClaw.API.Agents.Tools.Workspace;
using SharpClaw.API.Database;

namespace SharpClaw.API.Agents;

public class Agent(
    ChatProvider chatProvider,
    IConfiguration configuration,
    Repository repository,
    FragmentsRepository fragmentsRepository,
    WorkspaceRepository workspaceRepository)
{
    private readonly ConcurrentDictionary<Guid, AgentSessionState> _sessions = new();
    private readonly SemaphoreSlim _sessionsMutex = new(1, 1);

    public async Task<Guid> CreateSession(long agentId = 1)
    {
        var agentConfig = await repository.GetAgent(agentId)
                          ?? throw new KeyNotFoundException($"Agent {agentId} was not found.");
        var sessionId = Guid.NewGuid();

        var defaultWs = await workspaceRepository.ResolveDefaultWorkspace(agentId);
        var activeWorkspaces = new HashSet<string>();
        if (defaultWs is not null)
            activeWorkspaces.Add(defaultWs.Name);

        var context = new AgentExecutionContext
        {
            SessionId = sessionId,
            DbConnectionString = configuration.GetConnectionString("sharpclaw")!,
            AgentId = agentId,
            LlmModel = agentConfig.LlmModel,
            Temperature = agentConfig.Temperature,
            Messages = [],
            Workspace = defaultWs,
            ActiveWorkspaceNames = activeWorkspaces,
        };

        await repository.CreateSession(sessionId, agentId);
        _sessions[sessionId] = new AgentSessionState(sessionId, context);
        return sessionId;
    }

    public async Task<AgentRunState> EnqueueMessage(Guid sessionId, string prompt)
    {
        var session = await GetOrLoadSession(sessionId);

        AgentRunState run;
        lock (session.RunsLock)
        {
            var hasActiveRun = session.Runs.Values.Any(r => r.Status is AgentRunStatus.Pending or AgentRunStatus.Running);
            if (hasActiveRun)
                throw new InvalidOperationException($"Session {sessionId} already has an active run.");

            run = new AgentRunState(Guid.NewGuid(), sessionId);
            session.Runs[run.RunId] = run;
        }

        _ = Task.Run(() => GetMessageResponse(sessionId, prompt, session, run));

        return run;
    }

    public async Task<string?> GetMessageResponse(Guid sessionId, string prompt)
    {
        var session = await GetOrLoadSession(sessionId);

        AgentRunState run;
        lock (session.RunsLock)
        {
            var hasActiveRun = session.Runs.Values.Any(r => r.Status is AgentRunStatus.Pending or AgentRunStatus.Running);
            if (hasActiveRun)
                throw new InvalidOperationException($"Session {sessionId} already has an active run.");

            run = new AgentRunState(Guid.NewGuid(), sessionId);
            session.Runs[run.RunId] = run;
        }

        return await GetMessageResponse(sessionId, prompt, session, run);
    }

    private async Task<string?> GetMessageResponse(Guid sessionId, string prompt, AgentSessionState session, AgentRunState run)
    {
        await session.Mutex.WaitAsync();
        run.MarkStarted();
        try
        {
            AgentClientResponse? response = null;
            var userMessage = new ChatResponse(
                new ChatMessage(ChatRole.User, prompt)
                {
                    MessageId = Guid.NewGuid().ToString().Replace("-", ""),
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["lcm_type"] = "user_message",
                    },
                });

            await repository.PersistMessage(sessionId, run.RunId, userMessage);
            session.Context.Messages.Add(userMessage);

            while (response is null or { ShouldContinue: true })
            {
                var agentsMd = (await fragmentsRepository.ReadFragmentByPath(session.Context.AgentId, "AGENTS.md"))?.Content ?? string.Empty;
                var rootFragment = await fragmentsRepository.EnsureRootFragment(session.Context.AgentId);
                var fragments = await fragmentsRepository.ReadFragment(
                    session.Context.AgentId,
                    rootFragment,
                    includeChildren: true,
                    maxDepth: 1,
                    childNamesOnly: true);

                var systemMessage = string.Join('\n', [
                    Environment.EnvPrompt(session.Context.LlmModel, DateTimeOffset.Now, rootFragment, fragments?.Children, session.Context.Workspace),
                    Prompts.LcmPrompt,
                    Memory.Fragments.Prompts.FragmentPrompt,
                    agentsMd,
                ]);

                var agent = chatProvider.GetClient(session.Context);
                response = await agent.GetResponse(
                    [
                        new ChatMessage(ChatRole.System, systemMessage),
                        ..session.Context.Messages.SelectMany(r => r.Messages)
                    ],
                    BuildTools(),
                    run,
                    update =>
                    {
                        if (!string.IsNullOrEmpty(update.Text))
                            run.AppendDelta(update.Text);

                        foreach (var content in update.Contents)
                        {
                            switch (content)
                            {
                                case FunctionCallContent functionCall:
                                    run.AppendToolCall(
                                        functionCall.CallId,
                                        functionCall.Name,
                                        SerializeToolPayload(functionCall.Arguments));
                                    break;
                                case FunctionResultContent functionResult:
                                    run.AppendToolResult(
                                        functionResult.CallId,
                                        SerializeToolPayload(functionResult.Result));
                                    break;
                            }
                        }

                        return Task.CompletedTask;
                    });

                foreach (var message in response.Responses)
                {
                    await repository.PersistMessage(sessionId, run.RunId, message);
                }

                session.Context.Messages = [..session.Context.Messages, ..response.Responses];

                if (!response.ShouldContinue)
                    run.MarkCompleted(); // TODO: rework this

                var inputTokens = response.Responses.LastOrDefault(m => m.Usage is not null)?.Usage?.InputTokenCount;

                if (inputTokens is not null && inputTokens > session.Context.SoftCompactThreshold)
                {
                    _ = Task.Run(SoftCompactHistory(sessionId, session, run));
                }
            }

            return response.Responses.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Text))?.Text;
        }
        catch (Exception ex)
        {
            run.MarkFailed(ex.Message);
        }
        finally
        {
            session.Mutex.Release();
        }

        return null; // TODO: return error from here? possibly yes
    }

    private Func<Task?> SoftCompactHistory(Guid sessionId, AgentSessionState session, AgentRunState run)
    {
        return async () =>
        {
            var split = Summarizer.SplitMessages(session.Context.Messages, session.Context.FreshMessagesCount);

            if (split.Depth < 0) return; // TODO: handle failure case for split message

            var summarizer = new Summarizer(chatProvider);
            var summary = await summarizer.Summarize(session.Context, [], split.ToSummarize, split.Depth);
            await session.Mutex.WaitAsync();
            try
            {
                if (split.PreSummary.All(m => session.Context.Messages.Contains(m))
                    && split.ToSummarize.All(m => session.Context.Messages.Contains(m))
                    && split.PostSummary.All(m => session.Context.Messages.Contains(m)))
                {
                    await repository.PersistSummaryAndCompactHistory(
                        sessionId,
                        run.RunId,
                        summary,
                        split.ToSummarize);

                    session.Context.Messages = [..split.PreSummary, summary, ..split.PostSummary];
                }
            }
            finally
            {
                session.Mutex.Release();
            }
        };
    }

    public async Task<SessionHistoryDto> GetHistory(Guid sessionId)
    {
        var session = await GetOrLoadSession(sessionId);

        var runsByCreatedAt = session.Runs.Values
            .OrderBy(r => r.CreatedAt)
            .ToArray();

        var runsById = session.Runs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var activeRun = runsByCreatedAt.LastOrDefault(r => r.Status is AgentRunStatus.Pending or AgentRunStatus.Running);

        var rawMessages = await repository.LoadRawMessages(sessionId);
        var messages = new List<SessionMessageDto>(rawMessages.Count);

        foreach (var raw in rawMessages)
        {
            foreach (var message in raw.Response.Messages)
            {
                var runStatus = raw.RunId is not null && runsById.TryGetValue(raw.RunId.Value, out var run)
                    ? run.Status.ToString().ToLowerInvariant()
                    : null;

                messages.Add(new SessionMessageDto(
                    Role: message.Role.Value,
                    Text: message.Text,
                    Contents: GetMessageContents(message),
                    AuthorName: message.AuthorName,
                    RunId: raw.RunId,
                    RunStatus: runStatus
                ));
            }
        }

        return new SessionHistoryDto(
            SessionId: sessionId,
            ActiveRunId: activeRun?.RunId,
            ActiveRunStatus: activeRun?.Status.ToString().ToLowerInvariant(),
            Messages: messages
        );
    }

    public async Task<IReadOnlyList<AgentSessionDto>> GetSessions(long agentId)
    {
        var sessions = await repository.GetSessions(agentId);
        return sessions
            .Select(s => new AgentSessionDto(
                SessionId: s.SessionId,
                AgentId: s.AgentId,
                CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc)),
                MessagesCount: checked((int)s.MessagesCount)))
            .ToArray();
    }

    public AgentRunState GetRun(Guid sessionId, Guid runId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session {sessionId} was not found.");

        if (!session.Runs.TryGetValue(runId, out var run))
            throw new KeyNotFoundException($"Run {runId} was not found in session {sessionId}.");

        return run;
    }

    public AgentRunState? GetActiveRunForSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        return session.Runs.Values.FirstOrDefault(r => r.Status is AgentRunStatus.Pending or AgentRunStatus.Running);
    }

    public bool TryUpdateSessionActiveWorkspaces(Guid sessionId, string[] workspaceNames)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        session.Context.ActiveWorkspaceNames = new HashSet<string>(workspaceNames, StringComparer.OrdinalIgnoreCase);
        return true;
    }

    private async Task<AgentSessionState> GetOrLoadSession(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
            return existing;

        await _sessionsMutex.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(sessionId, out existing))
                return existing;

            var persistedSession = await repository.GetSession(sessionId)
                                   ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");
            var agentConfig = await repository.GetAgent(persistedSession.AgentId)
                              ?? throw new KeyNotFoundException($"Agent {persistedSession.AgentId} was not found.");

            var defaultWs = await workspaceRepository.ResolveDefaultWorkspace(persistedSession.AgentId);
            var activeWsRows = await workspaceRepository.GetActiveWorkspacesForSession(sessionId);
            var activeWsNames = new HashSet<string>(activeWsRows.Select(r => r.WorkspaceName));

            if (activeWsNames.Count == 0 && defaultWs is not null)
            {
                activeWsNames.Add(defaultWs.Name);
            }

            var context = new AgentExecutionContext
            {
                SessionId = sessionId,
                DbConnectionString = configuration.GetConnectionString("sharpclaw")!,
                AgentId = persistedSession.AgentId,
                LlmModel = agentConfig.LlmModel,
                Temperature = agentConfig.Temperature,
                Messages = [..await repository.LoadActiveConversation(sessionId)],
                Workspace = defaultWs,
                ActiveWorkspaceNames = activeWsNames,
            };

            var loadedSession = new AgentSessionState(
                sessionId,
                context,
                new DateTimeOffset(DateTime.SpecifyKind(persistedSession.CreatedAt, DateTimeKind.Utc)));
            _sessions[sessionId] = loadedSession;
            return loadedSession;
        }
        finally
        {
            _sessionsMutex.Release();
        }
    }

    private static List<AIFunction> BuildTools() =>
    [
        ..FragmentTools.Functions,
        ..LcmTools.Functions,
        ..WorkspaceTools.Functions,
        ..CommandTools.Functions,
        ..SessionWorkspaceTools.Functions,
        TasksTools.TaskTool([("Main", "the main agent")]), // TODO: get these agents from where?
    ];

    private static string? SerializeToolPayload(object? payload)
    {
        if (payload is null)
            return null;

        if (payload is string text)
            return text;

        try
        {
            return JsonSerializer.Serialize(payload);
        }
        catch
        {
            return payload.ToString();
        }
    }

    private static IReadOnlyList<SessionMessageContentDto> GetMessageContents(ChatMessage message)
    {
        if (message.Contents.Count == 0)
        {
            return string.IsNullOrWhiteSpace(message.Text)
                ? []
                : [new SessionMessageContentDto(Type: "text", Text: message.Text)];
        }

        var hasTextContent = message.Contents.Any(c => c is TextContent);
        var contents = new List<SessionMessageContentDto>(message.Contents.Count + 1);

        if (!hasTextContent && !string.IsNullOrWhiteSpace(message.Text))
            contents.Add(new SessionMessageContentDto(Type: "text", Text: message.Text));

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                    contents.Add(new SessionMessageContentDto(Type: "text", Text: textContent.Text));
                    break;
                case TextContent:
                    // Ignore empty text chunks emitted alongside tool-only messages.
                    break;
                case FunctionCallContent functionCall:
                    contents.Add(new SessionMessageContentDto(
                        Type: "tool_call",
                        CallId: functionCall.CallId,
                        ToolName: functionCall.Name,
                        Arguments: SerializeToolPayload(functionCall.Arguments)));
                    break;
                case FunctionResultContent functionResult:
                    contents.Add(new SessionMessageContentDto(
                        Type: "tool_result",
                        CallId: functionResult.CallId,
                        Result: SerializeToolPayload(functionResult.Result)));
                    break;
                default:
                    contents.Add(new SessionMessageContentDto(
                        Type: "unknown",
                        Payload: SerializeToolPayload(content)));
                    break;
            }
        }

        return contents;
    }

}

public class AgentSessionState(Guid sessionId, AgentExecutionContext context, DateTimeOffset? createdAt = null)
{
    public Guid SessionId { get; } = sessionId;
    public DateTimeOffset CreatedAt { get; } = createdAt ?? DateTimeOffset.UtcNow;
    public AgentExecutionContext Context { get; } = context;
    public SemaphoreSlim Mutex { get; } = new(1, 1);
    public object RunsLock { get; } = new();
    public ConcurrentDictionary<Guid, AgentRunState> Runs { get; } = new();
}

public enum AgentRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

public record AgentRunEvent(long Sequence, string Type, string? Text, DateTimeOffset Timestamp, object? Data = null);

public class AgentRunState(Guid runId, Guid sessionId)
{
    private readonly object _eventsLock = new();
    private readonly List<AgentRunEvent> _events = [];
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingApprovals = [];
    private long _sequence = 0;

    public Guid RunId { get; } = runId;
    public Guid SessionId { get; } = sessionId;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public AgentRunStatus Status { get; private set; } = AgentRunStatus.Pending;
    public string? Error { get; private set; }

    public AgentRunEvent[] GetEventsAfter(long sequence)
    {
        lock (_eventsLock)
        {
            return _events.Where(e => e.Sequence > sequence).ToArray();
        }
    }

    public void MarkStarted()
    {
        StartedAt = DateTimeOffset.UtcNow;
        Status = AgentRunStatus.Running;
        AddEvent("started", null);
    }

    public void AppendDelta(string text) => AddEvent("delta", text);
    public void AppendToolCall(string? callId, string? toolName, string? arguments) =>
        AddEvent("tool_call", null, new
        {
            callId,
            toolName,
            arguments,
        });

    public void AppendToolResult(string? callId, string? result) =>
        AddEvent("tool_result", null, new
        {
            callId,
            result,
        });

    public void MarkCompleted()
    {
        CompletedAt = DateTimeOffset.UtcNow;
        Status = AgentRunStatus.Completed;
        AddEvent("completed", null);
    }

    public void MarkFailed(string error)
    {
        CompletedAt = DateTimeOffset.UtcNow;
        Status = AgentRunStatus.Failed;
        Error = error;
        AddEvent("failed", error);
    }

    public TaskCompletionSource<bool> CreateApprovalRequest(string token, string action, string? target, string? commandPreview, string risk, string description)
    {
        var tcs = new TaskCompletionSource<bool>();
        lock (_eventsLock)
        {
            _pendingApprovals[token] = tcs;
            AddEvent("approval_required", null, new
            {
                approval_token = token,
                action,
                target,
                command_preview = commandPreview,
                risk,
                description,
            });
        }
        return tcs;
    }

    public bool ResolveApproval(string token, bool approved)
    {
        lock (_eventsLock)
        {
            if (_pendingApprovals.TryGetValue(token, out var tcs))
            {
                _pendingApprovals.Remove(token);
                return tcs.TrySetResult(approved);
            }
        }
        return false;
    }

    public void ExpireApproval(string token)
    {
        lock (_eventsLock)
        {
            if (_pendingApprovals.TryGetValue(token, out var tcs))
            {
                _pendingApprovals.Remove(token);
                tcs.TrySetCanceled();
            }
        }
    }

    private void AddEvent(string type, string? text, object? data = null)
    {
        lock (_eventsLock)
        {
            _sequence += 1;
            _events.Add(new AgentRunEvent(_sequence, type, text, DateTimeOffset.UtcNow, data));
        }
    }
}

public record AgentSessionDto(Guid SessionId, long AgentId, DateTimeOffset CreatedAt, int MessagesCount);
public record SessionHistoryDto(
    Guid SessionId,
    Guid? ActiveRunId,
    string? ActiveRunStatus,
    IReadOnlyList<SessionMessageDto> Messages
);

public record SessionMessageDto(
    string Role,
    string? Text,
    IReadOnlyList<SessionMessageContentDto> Contents,
    string? AuthorName,
    Guid? RunId,
    string? RunStatus
);

public record SessionMessageContentDto(
    string Type,
    string? Text = null,
    string? CallId = null,
    string? ToolName = null,
    string? Arguments = null,
    string? Result = null,
    string? Payload = null
);