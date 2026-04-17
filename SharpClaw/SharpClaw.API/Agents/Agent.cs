using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Memory.Lcm;
using SharpClaw.API.Agents.Tools.Fragments;
using SharpClaw.API.Agents.Tools.Lcm;
using SharpClaw.API.Agents.Tools.Tasks;
using SharpClaw.API.Agents.Tools.Workspace;
using SharpClaw.API.Database;
using SharpClaw.API.Helpers;

namespace SharpClaw.API.Agents;

public class Agent(
    ChatProvider chatProvider,
    Repository repository,
    FragmentsRepository fragmentsRepository,
    WorkspaceRepository workspaceRepository)
{
    private readonly ConcurrentDictionary<Guid, AgentSessionState> _sessions = new();
    private readonly SemaphoreSlim _sessionsMutex = new(1, 1);

    public async Task<Guid> CreateSession(long agentId = 1, Guid? parentSessionId = null)
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
            AgentId = agentId,
            LlmModel = agentConfig.LlmModel,
            Temperature = agentConfig.Temperature,
            Messages = [],
            Workspace = defaultWs,
            ActiveWorkspaceNames = activeWorkspaces,
        };

        await repository.CreateSession(sessionId, agentId, parentSessionId);
        _sessions[sessionId] = new AgentSessionState(sessionId, context, parentSessionId: parentSessionId);
        return sessionId;
    }

    // TODO: get a context as param to persist message inside transaction
    // TODO: move task.run to a separate method
    public async Task<AgentRunState> EnqueueMessage(Guid sessionId, string prompt)
    {
        var session = await GetOrLoadSession(sessionId);

        lock (session.RunsLock)
        {
            var hasActiveRun = session.Run is { Status: AgentRunStatus.Pending or AgentRunStatus.Running };
            if (hasActiveRun)
                throw new InvalidOperationException($"Session {sessionId} already has an active run.");

            // session.Run ??= new AgentRunState(sessionId);
        }

        await session.Mutex.WaitAsync();

        try
        {
            var userMessage = new ChatResponse(
                new ChatMessage(ChatRole.User, prompt)
                {
                    MessageId = Guid.NewGuid().ToString().Replace("-", ""),
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["lcm_type"] = "user_message",
                    },
                });

            await repository.PersistMessage(sessionId, userMessage);
            await repository.UpdateSession(sessionId, AgentRunStatus.Pending);
            session.Context.Messages.Add(userMessage);
        }
        catch (Exception ex)
        {
            session.Run.MarkFailed(ex.Message);
            throw;
        }
        finally
        {
            session.Mutex.Release();
        }

        _ = Task.Run(() => GetMessageResponse(sessionId, session, session.Run));

        return session.Run;
    }

    private async Task<string?> GetMessageResponse(Guid sessionId, AgentSessionState session, AgentRunState run)
    {
        await session.Mutex.WaitAsync();

        try
        {
            if (session.Run is null)
                throw new InvalidOperationException($"Session {sessionId} has no Run.");

            session.Run.MarkStarted(session.Context.MaxSequenceId() + 1); // TODO: move this inside running task

            AgentClientResponse? response = null;

            while (response is null or { ShouldContinue: true, QueuedTasks.Count: 0 })
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
                    run);

                // TODO: add transaction here
                foreach (var message in response.Responses)
                {
                    await repository.PersistMessage(sessionId, message);
                }
                session.Context.Messages = [..session.Context.Messages, ..response.Responses];

                foreach (var queuedTask in response.QueuedTasks)
                {
                    if (queuedTask.Type == AgentClientTask.TaskType.ChildSession)
                    {
                        if (queuedTask.ChildPrompt is null)
                            // TODO: improve error message
                            throw new Exception("ChildPrompt cannot be null when task is of type ChildSession");

                        var childSessionId = await CreateSession(
                            queuedTask.AgentId ?? session.Context.AgentId,
                            parentSessionId: sessionId);
                        await repository.AddSessionTask(sessionId, queuedTask.CallId, childSessionId);
                        session.Run.SessionDependencies.Add(new SessionDependency(childSessionId, queuedTask.CallId));
                        session.Run.AppendChildSessionSpawned(queuedTask.CallId, childSessionId, queuedTask.ChildDescription);
                        var taskResultMessage = AttachChildSessionMetadataToToolResult(
                            session.Context.Messages,
                            queuedTask.CallId,
                            childSessionId,
                            status: "running",
                            output: null,
                            description: queuedTask.ChildDescription);
                        if (taskResultMessage is not null)
                            await repository.UpdateMessage(taskResultMessage);
                        await EnqueueMessage(childSessionId, queuedTask.ChildPrompt);
                    }
                }

                if (response.QueuedTasks.Count > 0)
                {
                    await repository.UpdateSession(sessionId, AgentRunStatus.Waiting);
                } else if (response is not { ShouldContinue: true, QueuedTasks.Count: 0 })
                {
                    run.MarkCompleted(); // TODO: rework this
                    await repository.UpdateSession(sessionId, AgentRunStatus.Completed);

                    if (session.ParentSessionId is not null)
                    {
                        var taskResult = response.Responses.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Text))?.Text;
                        var callId = await repository.CompleteSessionTask(
                            sessionId,
                            session.ParentSessionId.Value,
                            taskResult);

                        // _ = Task.Run(async () =>
                        // {
                        var parentSession = await GetOrLoadSession(session.ParentSessionId.Value);
                        await parentSession.Mutex.WaitAsync();
                        try
                        {
                            var dep = parentSession.Run?.SessionDependencies.SingleOrDefault(d => d.CallId == callId);

                            if (dep is null)
                                throw new Exception("Parent content from task not found");

                            var message = parentSession.Context.Messages
                                .FirstOrDefault(r => r.Messages
                                    .SelectMany(m => m.Contents)
                                    .Any(m => m is FunctionResultContent trc
                                              && trc.CallId == callId));

                            if (message is null)
                                throw new Exception("Parent content from task not found");

                            var content = message.Messages
                                .SelectMany(m => m.Contents)
                                .FirstOrDefault(m => m is FunctionResultContent trc
                                                     && trc.CallId == callId) as FunctionResultContent;

                            if (content is null)
                                throw new Exception("Parent content from task not found");

                            var toolResult = BuildTaskToolResult(
                                sessionId,
                                status: "completed",
                                output: taskResult);
                            content.Result = toolResult;
                            await repository.UpdateMessage(message);
                            parentSession.Run?.AppendToolResultForCall(callId, Serialization.SerializeToolPayload(toolResult));

                            parentSession.Run!.SessionDependencies.Remove(dep);

                            if (parentSession.Run.SessionDependencies.Count == 0)
                                _ = Task.Run(() => GetMessageResponse(parentSession.SessionId, parentSession, parentSession.Run));
                        }
                        finally
                        {
                            parentSession.Mutex.Release();
                        }
                        // });
                    }
                }

                // TODO: transaction ends here

                var inputTokens = response.Responses.LastOrDefault(m => m.Usage is not null)?.Usage?.InputTokenCount;
                if (inputTokens is null)
                    continue;

                if (inputTokens > session.Context.HardCompactThreshold)
                {
                    session.Run.CompactionTask ??= Task.Run(SoftCompactHistory(sessionId, session));
                    session.Mutex.Release();
                    await session.Run.CompactionTask;
                    await session.Mutex.WaitAsync();
                }
                else if (inputTokens > session.Context.SoftCompactThreshold
                         && session.Run.CompactionTask is null)
                {
                    session.Run.CompactionTask = Task.Run(SoftCompactHistory(sessionId, session));
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

    private Func<Task?> SoftCompactHistory(Guid sessionId, AgentSessionState session)
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
        var childSessions = await repository.GetSessionTaskLinks(sessionId);

        var rawMessages = await repository.LoadRawMessages(sessionId);
        var messages = new List<SessionMessageDto>(rawMessages.Count);

        foreach (var raw in rawMessages)
        {
            foreach (var message in raw.Response.Messages)
            {
                var sequenceId = raw.Response.AdditionalProperties?[Constants.SequenceIdKey] as long?
                                 ?? throw new UnreachableException("all messages should have a sequenceId");

                messages.Add(new SessionMessageDto(
                    Role: message.Role.Value,
                    Text: message.Text,
                    Contents: GetMessageContents(message),
                    AuthorName: message.AuthorName,
                    MessageId: sequenceId
                ));
            }
        }

        return new SessionHistoryDto(
            SessionId: sessionId,
            ParentSessionId: session.ParentSessionId,
            LatestSequenceId: session.Context.Messages.LastOrDefault()?.AdditionalProperties?[Constants.SequenceIdKey] as long? ?? 0, // TODO: check this
            RunStatus: session.Run?.Status.ToString().ToLowerInvariant(),
            Messages: messages,
            ChildSessions: childSessions
                .Select(link => new SessionChildLinkDto(link.CallId, link.ChildSessionId, link.Completed))
                .ToArray()
        );
    }

    public async Task<IReadOnlyList<AgentSessionDto>> GetSessions(long agentId)
    {
        var sessions = await repository.GetSessions(agentId);
        return sessions
            .Select(s => new AgentSessionDto(
                SessionId: s.SessionId,
                AgentId: s.AgentId,
                ParentSessionId: s.ParentSessionId,
                CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc)),
                MessagesCount: checked((int)s.MessagesCount)))
            .ToArray();
    }

    public AgentRunState? GetActiveRunForSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        return session.Run is { Status: AgentRunStatus.Pending or AgentRunStatus.Running }
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

    public async Task<AgentSessionState> GetOrLoadSession(Guid sessionId)
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

            // TODO: load pending dependencies

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
                Messages = [..await repository.LoadActiveConversation(sessionId)],
                Workspace = defaultWs,
                ActiveWorkspaceNames = activeWsNames,
            };

            var loadedSession = new AgentSessionState(
                sessionId,
                context,
                parentSessionId: persistedSession.ParentSessionId,
                createdAt: new DateTimeOffset(DateTime.SpecifyKind(persistedSession.CreatedAt, DateTimeKind.Utc)));
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
                        Arguments: Serialization.SerializeToolPayload(functionCall.Arguments)));
                    break;
                case FunctionResultContent functionResult:
                    contents.Add(new SessionMessageContentDto(
                        Type: "tool_result",
                        CallId: functionResult.CallId,
                        Result: Serialization.SerializeToolPayload(functionResult.Result)));
                    break;
                default:
                    contents.Add(new SessionMessageContentDto(
                        Type: "unknown",
                        Payload: Serialization.SerializeToolPayload(content)));
                    break;
            }
        }

        return contents;
    }

    private static ChatResponse? AttachChildSessionMetadataToToolResult(
        IEnumerable<ChatResponse> responses,
        string callId,
        Guid childSessionId,
        string status,
        string? output,
        string? description = null)
    {
        var target = FindFunctionResult(responses, callId);
        if (target is null)
            return null;

        target.Value.Content.Result = BuildTaskToolResult(
            childSessionId,
            status,
            output,
            description);
        return target.Value.Message;
    }

    private static (ChatResponse Message, FunctionResultContent Content)? FindFunctionResult(
        IEnumerable<ChatResponse> responses,
        string callId)
    {
        foreach (var response in responses.Reverse())
        {
            var content = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .FirstOrDefault(m => string.Equals(m.CallId, callId, StringComparison.Ordinal));

            if (content is not null)
                return (response, content);
        }

        return null;
    }

    private static object BuildTaskToolResult(Guid childSessionId, string status, string? output, string? description = null) => new
    {
        type = "child_session",
        child_session_id = childSessionId,
        status,
        description,
        output,
    };

}

public class AgentSessionState(Guid sessionId, AgentExecutionContext context,
    Guid? parentSessionId = null,
    DateTimeOffset? createdAt = null)
{
    public Guid SessionId { get; } = sessionId;
    public Guid? ParentSessionId { get; } = parentSessionId;
    public DateTimeOffset CreatedAt { get; } = createdAt ?? DateTimeOffset.UtcNow;
    public AgentExecutionContext Context { get; } = context;
    public SemaphoreSlim Mutex { get; } = new(1, 1);
    public object RunsLock { get; } = new();
    public AgentRunState Run { get; } = new(sessionId);
}

// TODO: check all status transitions, they are probably wrong in places
public enum AgentRunStatus
{
    Pending,
    Waiting,
    Running,
    Completed,
    Failed,
}

public record AgentRunEvent(long MessageId, long Sequence, string Type, string? Text, DateTimeOffset Timestamp, object? Data = null);

public record SessionDependency(Guid ChildSessionId, string CallId);

public class AgentRunState(Guid sessionId)
{
    private readonly object _eventsLock = new();
    private readonly List<AgentRunEvent> _events = [];
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingApprovals = [];
    private long _currentMessageId = 0;
    private long _sequence = 0;

    public Guid SessionId { get; } = sessionId;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; private set; }
    public long StartMessageId { get; set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public AgentRunStatus Status { get; private set; } = AgentRunStatus.Completed;
    public string? Error { get; private set; }
    public List<SessionDependency> SessionDependencies = [];
    public Task? CompactionTask { get; set; } = null;

    public AgentRunEvent[] GetEventsAfter(long messageId, long sequence)
    {
        lock (_eventsLock)
        {
            return _events.Where(e => e.MessageId >= messageId && e.Sequence > sequence).ToArray();
        }
    }

    public void MarkStarted(long messageId)
    {
        _currentMessageId = messageId;
        StartedAt = DateTimeOffset.UtcNow;
        StartMessageId = messageId;
        Status = AgentRunStatus.Running;
        AddEvent(messageId, "started", null);
    }

    public void NextMessage()
    {
        _currentMessageId++;
    }

    public void AppendUpdate(ChatResponseUpdate update)
    {
        if (!string.IsNullOrEmpty(update.Text))
            AppendDelta(update.Text);

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent functionCall:
                    AppendToolCall(
                        functionCall.CallId,
                        functionCall.Name,
                        Serialization.SerializeToolPayload(functionCall.Arguments));
                    break;
                case FunctionResultContent functionResult:
                    AppendToolResult(
                        functionResult.CallId,
                        Serialization.SerializeToolPayload(functionResult.Result));
                    break;
            }
        }
    }

    private void AppendDelta(string text) => AddEvent(_currentMessageId, "delta", text);
    private void AppendToolCall(string? callId, string? toolName, string? arguments) =>
        AddEvent(_currentMessageId, "tool_call", null, new
        {
            callId,
            toolName,
            arguments,
        });

    private void AppendToolResult(string? callId, string? result) =>
        AddEvent(_currentMessageId, "tool_result", null, new
        {
            callId,
            result,
        });

    public void AppendToolResultForCall(string callId, string? result) =>
        AppendToolResult(callId, result);

    public void AppendChildSessionSpawned(string callId, Guid childSessionId, string? description) =>
        AddEvent(_currentMessageId, "child_session_spawned", null, new
        {
            callId,
            childSessionId,
            description,
        });

    public void MarkCompleted()
    {
        Console.WriteLine($"completing session {sessionId}");
        CompletedAt = DateTimeOffset.UtcNow;
        Status = AgentRunStatus.Completed;
        AddEvent(_events.Max(e => e.MessageId), "completed", null);
    }

    public void MarkFailed(string error)
    {
        CompletedAt = DateTimeOffset.UtcNow;
        Status = AgentRunStatus.Failed;
        Error = error;
        AddEvent(_currentMessageId, "failed", error);
    }

    public TaskCompletionSource<bool> CreateApprovalRequest(string token, string action, string? target, string? commandPreview, string risk, string description)
    {
        var tcs = new TaskCompletionSource<bool>();
        lock (_eventsLock)
        {
            _pendingApprovals[token] = tcs;
            AddEvent(_currentMessageId, "approval_required", null, new
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

    // TODO: what is this method for?
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

    private void AddEvent(long messageId, string type, string? text, object? data = null)
    {
        // TODO: purge old events, keep only the ones relevant to active conversation + some buffer (how much?)
        lock (_eventsLock)
        {
            _sequence += 1;
            _events.Add(new AgentRunEvent(messageId, _sequence, type, text, DateTimeOffset.UtcNow, data));
        }
    }
}

public record AgentSessionDto(Guid SessionId, long AgentId, Guid? ParentSessionId, DateTimeOffset CreatedAt, int MessagesCount);
public record SessionHistoryDto(
    Guid SessionId,
    Guid? ParentSessionId,
    long LatestSequenceId,
    string? RunStatus,
    IReadOnlyList<SessionMessageDto> Messages,
    IReadOnlyList<SessionChildLinkDto> ChildSessions
);

public record SessionMessageDto(
    string Role,
    string? Text,
    IReadOnlyList<SessionMessageContentDto> Contents,
    string? AuthorName,
    long MessageId
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

public record SessionChildLinkDto(
    string CallId,
    Guid ChildSessionId,
    bool Completed
);
