using Microsoft.Extensions.AI;
using SharpClaw.API.Helpers;

namespace SharpClaw.API.Agents;

// TODO: check all status transitions, they are probably wrong in places
public enum AgentRunStatus
{
    Pending,
    Waiting,
    Running,
    Completed,
    Failed,
}

public class AgentRunState(Guid sessionId, List<SessionDependency> sessionDependencies)
{
    private readonly object _eventsLock = new();
    private readonly List<AgentRunEvent> _events = [];
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingApprovals = [];
    private long _currentMessageId = 0;
    private long _sequence = 0;

    public Guid SessionId => sessionId;
    public DateTimeOffset? StartedAt { get; private set; }
    public long StartMessageId { get; set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public AgentRunStatus Status { get; private set; } = AgentRunStatus.Completed;
    public string? Error { get; private set; }
    public List<SessionDependency> SessionDependencies { get; private set; } = sessionDependencies;
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

public record AgentRunEvent(long MessageId, long Sequence, string Type, string? Text, DateTimeOffset Timestamp, object? Data = null);

public record SessionDependency(Guid ChildSessionId, string CallId);

public record AgentSessionDto(
    Guid SessionId,
    long AgentId,
    string? Name,
    bool VisibleInSidebar,
    Guid? ParentSessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessagesCount);
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
