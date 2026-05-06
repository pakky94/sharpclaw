using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Memory.Lcm;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database.Repositories;
using SharpClaw.API.Helpers;

namespace SharpClaw.API.Agents;

public class Agent(
    SessionStore sessionStore,
    ChatProvider chatProvider,
    ChatRepository chatRepository,
    FragmentsRepository fragmentsRepository,
    WorkspaceRepository workspaceRepository,
    AgentsRepository agentsRepository,
    IServiceProvider serviceProvider,
    ILogger<Agent> logger)
{
    public async Task<Guid> CreateSession(
        long agentId = 1,
        Guid? parentSessionId = null,
        string[]? workspaces = null,
        string? name = null,
        bool visibleInSidebar = true)
    {
        var agentConfig = await agentsRepository.GetAgent(agentId)
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
            SoftCompactThreshold = agentConfig.SoftCompactThreshold,
            HardCompactThreshold = agentConfig.HardCompactThreshold,
            Messages = [],
            Workspace = defaultWs,
            ActiveWorkspaceNames = activeWorkspaces,
        };

        await chatRepository.CreateSession(sessionId, agentId, parentSessionId, name, visibleInSidebar);
        if (workspaces is not null)
            await workspaceRepository.SetActiveWorkspacesForSession(sessionId, agentId, workspaces);
        await sessionStore.Add(new AgentSessionState(sessionId, context, parentSessionId: parentSessionId));
        return sessionId;
    }

    // TODO: get a context as param to persist message inside transaction
    // TODO: move task.run to a separate method
    public async Task<AgentRunState> EnqueueMessage(Guid sessionId, string prompt)
    {
        var session = await sessionStore.GetOrLoadSession(sessionId);

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

            await chatRepository.PersistMessage(sessionId, userMessage);
            await chatRepository.UpdateSession(sessionId, AgentRunStatus.Pending);
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
        if (!sessionStore.TryMarkRunExecuting(sessionId))
            return null;

        await session.Mutex.WaitAsync();

        try
        {
            if (session.Run is null)
                throw new InvalidOperationException($"Session {sessionId} has no Run.");

            ThrowIfStopRequested(run, sessionId);
            session.Run.MarkStarted(session.Context.MaxSequenceId() + 1); // TODO: move this inside running task

            AgentClientResponse? response = null;

            while (response is null or { ShouldContinue: true, QueuedTasks.Count: 0, QueuedApprovals.Count: 0 })
            {
                ThrowIfStopRequested(run, sessionId);
                await TryCompactHistory(session);

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
                ThrowIfStopRequested(run, sessionId);

                // TODO: add transaction here
                foreach (var message in response.Responses)
                {
                    await chatRepository.PersistMessage(sessionId, message);
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
                            parentSessionId: sessionId,
                            workspaces: session.Context.ActiveWorkspaceNames.ToArray(),
                            visibleInSidebar: false);
                        await chatRepository.AddSessionTask(sessionId, queuedTask.CallId, childSessionId);
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
                            await chatRepository.UpdateMessage(taskResultMessage);
                        ThrowIfStopRequested(run, sessionId);
                        await EnqueueMessage(childSessionId, queuedTask.ChildPrompt);
                    }
                }

                if (response.QueuedTasks.Count > 0 || response.QueuedApprovals.Count > 0)
                {
                    run.SetStatus(AgentRunStatus.Waiting);
                    await chatRepository.UpdateSession(sessionId, AgentRunStatus.Waiting);
                } else if (response is not { ShouldContinue: true, QueuedTasks.Count: 0, QueuedApprovals.Count: 0 })
                {
                    run.MarkCompleted(); // TODO: rework this
                    await chatRepository.UpdateSession(sessionId, AgentRunStatus.Completed);

                    if (session.ParentSessionId is not null)
                    {
                        var taskResult = response.Responses.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Text))?.Text;
                        var callId = await chatRepository.CompleteSessionTask(
                            sessionId,
                            session.ParentSessionId.Value,
                            taskResult);

                        var parentSession = await sessionStore.GetOrLoadSession(session.ParentSessionId.Value);
                        await parentSession.Mutex.WaitAsync();
                        try
                        {
                            var dep = parentSession.Run?.SessionDependencies.SingleOrDefault(d => d.CallId == callId);

                            if (dep is null)
                                throw new Exception($"Session dependency not found for callId {callId} in {JsonSerializer.Serialize(parentSession.Run?.SessionDependencies)}");

                            var message = parentSession.Context.Messages
                                .FirstOrDefault(r => r.Messages
                                    .SelectMany(m => m.Contents)
                                    .Any(m => m is FunctionResultContent trc
                                              && trc.CallId == callId));

                            if (message is null)
                                throw new Exception($"Message containing task result not found for callId {callId} in {JsonSerializer.Serialize(parentSession.Context.Messages)}");

                            var content = message.Messages
                                .SelectMany(m => m.Contents)
                                .FirstOrDefault(m => m is FunctionResultContent trc
                                                     && trc.CallId == callId) as FunctionResultContent;

                            if (content is null)
                                throw new Exception($"Task result content not found for callId {callId} in {JsonSerializer.Serialize(message.Messages)}");

                            var description = content.Result is IDictionary<string, object?> d
                                              && d.TryGetValue("description", out var desc)
                                              && desc is string value
                                ? value : null;

                            var toolResult = BuildTaskToolResult(
                                sessionId,
                                status: "completed",
                                output: taskResult,
                                description: description);
                            content.Result = toolResult;
                            await chatRepository.UpdateMessage(message);
                            parentSession.Run?.AppendToolResultForCall(callId, Serialization.SerializeToolPayload(toolResult));

                            parentSession.Run!.SessionDependencies.Remove(dep);

                            if (!parentSession.Run.IsStopRequested && parentSession.Run.SessionDependencies.Count == 0)
                                _ = Task.Run(() => GetMessageResponse(parentSession.SessionId, parentSession, parentSession.Run));
                        }
                        finally
                        {
                            parentSession.Mutex.Release();
                        }
                    }
                }

                // TODO: transaction ends here
                await TryCompactHistory(session, true);
            }

            return response.Responses.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Text))?.Text;
        }
        catch (Exception ex)
        {
            if (run.Status is not AgentRunStatus.Failed)
                run.MarkFailed(ex.Message);
            await chatRepository.UpdateSession(sessionId, AgentRunStatus.Failed);
            logger.LogWarning(ex, "Exception processing session {SessionId}", sessionId);
        }
        finally
        {
            session.Mutex.Release();
            sessionStore.ClearRunExecuting(sessionId);
        }

        return null; // TODO: return error from here? possibly yes
    }

    private async Task TryCompactHistory(AgentSessionState session, bool softThreshold = false)
    {
        if (session.Run.CompactionTask is { Status: TaskStatus.RanToCompletion })
        {
            session.Mutex.Release();
            await session.Run.CompactionTask;
            await session.Mutex.WaitAsync();
        }

        var inputTokens = session.Context.Messages.EstimatedTokenCount();

        if (inputTokens > session.Context.HardCompactThreshold)
        {
            session.Run.CompactionTask ??= Task.Run(SoftCompactHistory(session));
            session.Mutex.Release();
            await session.Run.CompactionTask;
            await session.Mutex.WaitAsync();
        }
        else if (softThreshold
                 && inputTokens > session.Context.SoftCompactThreshold
                 && session.Run.CompactionTask is null)
        {
            session.Run.CompactionTask = Task.Run(SoftCompactHistory(session));
        }
    }

    private Func<Task?> SoftCompactHistory(AgentSessionState session)
    {
        return async () =>
        {
            var split = Summarizer.SplitMessages(session.Context.Messages, session.Context.FreshMessagesCount);

            if (split.Depth < 0) return; // TODO: handle failure case for split message

            var agentClient = chatProvider.GetClient(session.Context);
            var summary = await Summarizer.Summarize(agentClient.GetResponse, [], split.ToSummarize, split.Depth);
            await session.Mutex.WaitAsync();
            try
            {
                if (split.PreSummary.All(m => session.Context.Messages.Contains(m))
                    && split.ToSummarize.All(m => session.Context.Messages.Contains(m))
                    && split.PostSummary.All(m => session.Context.Messages.Contains(m)))
                {
                    await chatRepository.PersistSummaryAndCompactHistory(
                        session.SessionId,
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
        var session = await sessionStore.GetOrLoadSession(sessionId);
        var childSessions = await chatRepository.GetSessionTaskLinks(sessionId);

        var rawMessages = await chatRepository.LoadRawMessages(sessionId);
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

    public async Task<PaginatedSessionHistoryDto> GetHistoryPaginated(
        Guid sessionId,
        int limit = 100,
        long? beforeSequence = null,
        int childLimit = 50,
        int childOffset = 0)
    {
        var session = await sessionStore.GetOrLoadSession(sessionId);

        var (rawMessages, hasMoreMessages) = await chatRepository.LoadRawMessagesPaginated(
            sessionId, limit, beforeSequence);

        var (childLinks, hasMoreChildSessions) = await chatRepository.GetSessionTaskLinksPaginated(
            sessionId, childLimit, childOffset);

        var totalMessageCount = await chatRepository.GetMessageCount(sessionId);

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

        return new PaginatedSessionHistoryDto(
            SessionId: sessionId,
            ParentSessionId: session.ParentSessionId,
            LatestSequenceId: session.Context.Messages.LastOrDefault()?.AdditionalProperties?[Constants.SequenceIdKey] as long? ?? 0,
            RunStatus: session.Run?.Status.ToString().ToLowerInvariant(),
            Messages: messages,
            ChildSessions: childLinks
                .Select(link => new SessionChildLinkDto(link.CallId, link.ChildSessionId, link.Completed))
                .ToArray(),
            HasMoreMessages: hasMoreMessages,
            HasMoreChildSessions: hasMoreChildSessions,
            TotalMessageCount: totalMessageCount
        );
    }

    public async Task<IReadOnlyList<AgentSessionDto>> GetSessions(long agentId)
    {
        var sessions = await chatRepository.GetSessions(agentId);
        return sessions
            .Select(s => new AgentSessionDto(
                SessionId: s.SessionId,
                AgentId: s.AgentId,
                Name: s.Name,
                VisibleInSidebar: s.VisibleInSidebar,
                ParentSessionId: s.ParentSessionId,
                CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc)),
                UpdatedAt: new DateTimeOffset(DateTime.SpecifyKind(s.UpdatedAt, DateTimeKind.Utc)),
                MessagesCount: checked((int)s.MessagesCount)))
            .ToArray();
    }

    public async Task<string?> RenameSession(Guid sessionId, string name)
    {
        var session = await chatRepository.RenameSession(sessionId, name)
                      ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");
        return session.Name;
    }

    private static List<AIFunction> BuildTools() => ToolCatalog.BuildTools();

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

    private static Dictionary<string, object?> BuildTaskToolResult(Guid childSessionId, string status, string? output, string? description) => new()
    {
        ["type"] = "child_session",
        ["child_session_id"] = childSessionId,
        ["status"] = status,
        ["description"] = description,
        ["output"] = output,
    };

    public async Task<ApprovalResolutionResult> ResolveWorkspaceApproval(Guid sessionId, string token, bool approved)
    {
        var approval = await workspaceRepository.GetApprovalEventByToken(token);
        if (approval is null || approval.Status != ApprovalStatus.Pending)
            return ApprovalResolutionResult.InvalidToken;

        var allowed = await chatRepository.IsAncestorOrSelf(sessionId, approval.SessionId);
        if (!allowed)
            return ApprovalResolutionResult.WrongSession;

        var resolved = await workspaceRepository.ResolveApprovalEvent(
            token,
            approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected);
        if (!resolved)
            return ApprovalResolutionResult.FailedToResolve;

        await ApplyApprovalOutcomeAndMaybeResume(approval, approved);
        return ApprovalResolutionResult.Resolved;
    }

    private async Task ApplyApprovalOutcomeAndMaybeResume(WorkspaceApprovalEvent approval, bool approved)
    {
        if (string.IsNullOrWhiteSpace(approval.CallId))
            return;

        var session = await sessionStore.GetOrLoadSession(approval.SessionId);
        var shouldResume = false;

        await session.Mutex.WaitAsync();
        try
        {
            var message = session.Context.Messages
                .FirstOrDefault(r => r.Messages
                    .SelectMany(m => m.Contents)
                    .Any(m => m is FunctionResultContent trc
                              && trc.CallId == approval.CallId));

            if (message is null)
                return;

            var content = message.Messages
                .SelectMany(m => m.Contents)
                .FirstOrDefault(m => m is FunctionResultContent trc
                                     && trc.CallId == approval.CallId) as FunctionResultContent;
            if (content is null)
                return;

            object toolResult;
            if (!approved)
            {
                toolResult = new
                {
                    error = $"Action was rejected by user: {approval.Description ?? approval.ActionType.ToString()}",
                };
            }
            else
            {
                toolResult = await ReplayApprovedToolCall(session, approval);
            }

            content.Result = toolResult;
            await chatRepository.UpdateMessage(message);
            session.Run.AppendToolResultForCall(approval.CallId, Serialization.SerializeToolPayload(toolResult));

            var pendingApprovals = await workspaceRepository.GetPendingApprovalsForSession(approval.SessionId);
            if (!session.Run.IsStopRequested
                && session.Run.Status == AgentRunStatus.Waiting
                && pendingApprovals.Count == 0
                && session.Run.SessionDependencies.Count == 0)
                shouldResume = true;
        }
        finally
        {
            session.Mutex.Release();
        }

        if (shouldResume)
            _ = Task.Run(() => GetMessageResponse(approval.SessionId, session, session.Run));
    }

    private async Task<object> ReplayApprovedToolCall(AgentSessionState session, WorkspaceApprovalEvent approval)
    {
        if (string.IsNullOrWhiteSpace(approval.ToolName))
        {
            return new { error = "Cannot replay approved tool call: missing tool name." };
        }

        var tools = BuildTools();
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, approval.ToolName, StringComparison.Ordinal));
        if (tool is null)
            return new { error = $"Cannot replay approved tool call: tool '{approval.ToolName}' is not registered." };

        Dictionary<string, object?> args;
        try
        {
            args = string.IsNullOrWhiteSpace(approval.ToolArguments)
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(approval.ToolArguments)
                  ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            return new { error = $"Cannot replay approved tool call: invalid stored tool arguments ({ex.Message})." };
        }

        args["approval_token"] = approval.ApprovalToken;

        var functionArguments = new AIFunctionArguments(args)
        {
            Services = new AgentClientServiceProvider(serviceProvider, session.Context, session.Run),
            Context = new Dictionary<object, object?>
            {
                ["CallId"] = approval.CallId,
            },
        };

        try
        {
            return await tool.InvokeAsync(functionArguments) ?? new { };
        }
        catch (Exception ex)
        {
            return new { error = $"Approved tool replay failed: {ex.Message}" };
        }
    }

    public async Task Resume(Guid sessionId)
    {
        var session = await sessionStore.GetOrLoadSession(sessionId);
        _ = Task.Run(() => GetMessageResponse(sessionId, session, session.Run));
    }

    public async Task<ResumeSessionsResult> ResumeIfPossible(Guid sessionId, bool includeDescendants = true)
    {
        var targetSessionIds = includeDescendants
            ? await chatRepository.GetSessionAndDescendantIds(sessionId)
            : [sessionId];

        var sessionsById = new Dictionary<Guid, AgentSessionState>();
        foreach (var id in targetSessionIds)
            sessionsById[id] = await sessionStore.GetOrLoadSession(id);

        var resumed = 0;
        var blockedByApprovals = 0;
        var blockedByDependencies = 0;
        var alreadyActive = 0;
        var notWaiting = 0;

        foreach (var id in targetSessionIds.Reverse())
        {
            var session = sessionsById[id];
            var run = session.Run;

            if (sessionStore.IsRunExecuting(id))
            {
                alreadyActive++;
                continue;
            }

            if (run.Status is AgentRunStatus.Completed or AgentRunStatus.Failed)
            {
                notWaiting++;
                continue;
            }

            if (run.IsStopRequested)
            {
                notWaiting++;
                continue;
            }

            var pendingApprovals = await workspaceRepository.GetPendingApprovalsForSession(id);
            if (pendingApprovals.Count > 0)
            {
                blockedByApprovals++;
                continue;
            }

            if (run.SessionDependencies.Count > 0)
            {
                blockedByDependencies++;
                continue;
            }

            run.SetStatus(AgentRunStatus.Pending);
            await chatRepository.UpdateSession(id, AgentRunStatus.Pending);
            _ = Task.Run(() => GetMessageResponse(id, session, run));
            resumed++;
        }

        return new ResumeSessionsResult(resumed, blockedByApprovals, blockedByDependencies, alreadyActive, notWaiting);
    }

    public async Task<StopSessionsResult> StopSession(Guid sessionId, bool includeDescendants = true)
    {
        var targetSessionIds = includeDescendants
            ? await chatRepository.GetSessionAndDescendantIds(sessionId)
            : [sessionId];

        var stopped = 0;
        foreach (var id in targetSessionIds)
        {
            var session = await sessionStore.GetOrLoadSession(id);
            session.Run.RequestStop("Session stopped by user.");
            session.Run.MarkFailed("Session stopped by user.");
            await chatRepository.UpdateSession(id, AgentRunStatus.Failed);
            stopped++;
        }

        return new StopSessionsResult(stopped);
    }

    private static void ThrowIfStopRequested(AgentRunState run, Guid sessionId)
    {
        if (!run.IsStopRequested)
            return;

        throw new OperationCanceledException($"Session {sessionId} was stopped by user.");
    }
}

public enum ApprovalResolutionResult
{
    Resolved,
    InvalidToken,
    WrongSession,
    FailedToResolve,
}

public record ResumeSessionsResult(
    int Resumed,
    int BlockedByApprovals,
    int BlockedByDependencies,
    int AlreadyActive,
    int NotWaiting);

public record StopSessionsResult(
    int Stopped);

public class AgentSessionState(Guid sessionId, AgentExecutionContext context,
    Guid? parentSessionId = null,
    DateTimeOffset? createdAt = null,
    List<SessionDependency>? sessionDependencies = null)
{
    public Guid SessionId { get; } = sessionId;
    public Guid? ParentSessionId { get; } = parentSessionId;
    public DateTimeOffset CreatedAt { get; } = createdAt ?? DateTimeOffset.UtcNow;
    public AgentExecutionContext Context { get; } = context;
    public SemaphoreSlim Mutex { get; } = new(1, 1);
    public object RunsLock { get; } = new();
    public AgentRunState Run { get; } = new(sessionId, sessionDependencies ?? []);
}
