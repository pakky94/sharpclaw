using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.AI;
using Npgsql;
using SharpClaw.API.Agents.Memory.Lcm;
using SharpClaw.API.Agents.Tools.Files;

namespace SharpClaw.API.Agents;

public class Agent(ChatProvider chatProvider, IConfiguration configuration)
{
    private readonly ConcurrentDictionary<Guid, AgentSessionState> _sessions = new();

    public async Task<Guid> CreateSession(long agentId = 1)
    {
        var systemPrompt = await GetAgentMd(agentId) ?? "";
        var sessionId = Guid.NewGuid();

        var context = new AgentExecutionContext
        {
            DbConnectionString = configuration.GetConnectionString("sharpclaw")!,
            AgentId = agentId,
            SystemMessage = new ChatMessage(ChatRole.System, systemPrompt),
            Messages =
            [
            ],
        };

        _sessions[sessionId] = new AgentSessionState(sessionId, context);
        return sessionId;
    }

    public Task<AgentRunState> EnqueueMessage(Guid sessionId, string prompt)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session {sessionId} was not found.");

        AgentRunState run;
        lock (session.RunsLock)
        {
            var hasActiveRun = session.Runs.Values.Any(r => r.Status is AgentRunStatus.Pending or AgentRunStatus.Running);
            if (hasActiveRun)
                throw new InvalidOperationException($"Session {sessionId} already has an active run.");

            run = new AgentRunState(Guid.NewGuid(), sessionId);
            session.Runs[run.RunId] = run;
        }

        _ = Task.Run(async () =>
        {
            await session.Mutex.WaitAsync();
            run.MarkStarted();
            try
            {
                session.Context.Messages.Add(new ChatMessage(ChatRole.User, prompt)
                {
                    MessageId = Guid.NewGuid().ToString().Replace("-", ""),
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["lcm_type"] = "user_message",
                    },
                });

                var agent = chatProvider.GetClient(session.Context);
                var response = await agent.GetResponse(
                    [session.Context.SystemMessage, ..session.Context.Messages],
                    BuildTools(),
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

                session.Context.Messages.AddRange(response);

                run.MarkCompleted();

                _ = Task.Run(async () =>
                {
                    var summarizer = new Summarizer(chatProvider);
                    _ = await summarizer.Summarize(session.Context, [], session.Context.Messages);
                });
            }
            catch (Exception ex)
            {
                run.MarkFailed(ex.Message);
            }
            finally
            {
                session.Mutex.Release();
            }
        });

        return Task.FromResult(run);
    }

    public SessionHistoryDto GetHistory(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session {sessionId} was not found.");

        var runsByCreatedAt = session.Runs.Values
            .OrderBy(r => r.CreatedAt)
            .ToArray();

        var runsById = session.Runs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var activeRun = runsByCreatedAt.LastOrDefault(r => r.Status is AgentRunStatus.Pending or AgentRunStatus.Running);

        var messages = new List<SessionMessageDto>(session.Context.Messages.Count);
        var runCursor = -1;
        Guid? currentRunId = null;

        foreach (var message in session.Context.Messages)
        {
            if (message.Role == ChatRole.User)
            {
                runCursor += 1;
                currentRunId = runCursor < runsByCreatedAt.Length
                    ? runsByCreatedAt[runCursor].RunId
                    : null;
            }

            var messageRunId = message.Role == ChatRole.System ? null : currentRunId;
            var runStatus = messageRunId is not null && runsById.TryGetValue(messageRunId.Value, out var run)
                ? run.Status.ToString().ToLowerInvariant()
                : null;

            messages.Add(new SessionMessageDto(
                Role: message.Role.Value,
                Text: message.Text,
                Contents: GetMessageContents(message),
                AuthorName: message.AuthorName,
                RunId: messageRunId,
                RunStatus: runStatus
            ));
        }

        return new SessionHistoryDto(
            SessionId: sessionId,
            ActiveRunId: activeRun?.RunId,
            ActiveRunStatus: activeRun?.Status.ToString().ToLowerInvariant(),
            Messages: messages
        );
    }

    public IReadOnlyList<AgentSessionDto> GetSessions(long agentId)
    {
        return _sessions.Values
            .Where(s => s.Context.AgentId == agentId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new AgentSessionDto(
                SessionId: s.SessionId,
                AgentId: s.Context.AgentId,
                CreatedAt: s.CreatedAt,
                MessagesCount: s.Context.Messages.Count
            ))
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

    private static List<AIFunction> BuildTools() =>
    [
        ..FileTools.Functions,
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

    private async Task<string?> GetAgentMd(long agentId)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("sharpclaw"));

        var content = await connection.QueryFirstOrDefaultAsync<string>(
            """
            select d.content from agents a
            join agents_documents ad on a.id = ad.agent_id
            join documents d on d.id = ad.document_id
            where a.Id = @agentId and d.name = 'AGENTS.md';
            """,
            new
            {
                agentId,
            });

        return content;
    }
}

public class AgentSessionState(Guid sessionId, AgentExecutionContext context)
{
    public Guid SessionId { get; } = sessionId;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
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
