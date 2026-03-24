using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.AI;
using Npgsql;

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
            Messages =
            [
                new ChatMessage(ChatRole.System, systemPrompt),
            ],
        };

        _sessions[sessionId] = new AgentSessionState(sessionId, context);
        return sessionId;
    }

    public async Task<AgentRunState> EnqueueMessage(Guid sessionId, string prompt)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session {sessionId} was not found.");

        var run = new AgentRunState(Guid.NewGuid(), sessionId);
        session.Runs[run.RunId] = run;

        _ = Task.Run(async () =>
        {
            await session.Mutex.WaitAsync();
            run.MarkStarted();
            try
            {
                session.Context.Messages.Add(new ChatMessage(ChatRole.User, prompt));

                var agent = chatProvider.GetClient(session.Context);
                await agent.GetStreamingResponse(BuildTools(), update =>
                {
                    if (!string.IsNullOrEmpty(update.Text))
                        run.AppendDelta(update.Text);

                    return Task.CompletedTask;
                });

                run.MarkCompleted();
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

        return run;
    }

    public IReadOnlyList<SessionMessageDto> GetHistory(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session {sessionId} was not found.");

        return session.Context.Messages
            .Select(message => new SessionMessageDto(
                Role: message.Role.Value,
                Text: message.Text,
                AuthorName: message.AuthorName
            ))
            .ToArray();
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
        AIFunctionFactory.Create(Tooling.ListFiles, "list_files", "Lists all files in your workspace"),
        AIFunctionFactory.Create(Tooling.ReadFile, "read_file", "Read a file from your workspace"),
        AIFunctionFactory.Create(Tooling.WriteFile, "write_file", "Write a file in your workspace, overwriting if it exists"),
        AIFunctionFactory.Create(Tooling.DeleteFile, "delete_file", "Delete a file from your workspace"),
    ];

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
    public ConcurrentDictionary<Guid, AgentRunState> Runs { get; } = new();
}

public enum AgentRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

public record AgentRunEvent(long Sequence, string Type, string? Text, DateTimeOffset Timestamp);

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

    private void AddEvent(string type, string? text)
    {
        lock (_eventsLock)
        {
            _sequence += 1;
            _events.Add(new AgentRunEvent(_sequence, type, text, DateTimeOffset.UtcNow));
        }
    }
}

public record AgentSessionDto(Guid SessionId, long AgentId, DateTimeOffset CreatedAt, int MessagesCount);
public record SessionMessageDto(string Role, string? Text, string? AuthorName);
