using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Agents;

public class AgentExecutionContext
{
    public required Guid SessionId { get; set; }
    public required string DbConnectionString { get; set; }
    public required long AgentId { get; set; }
    public required string LlmModel { get; set; }
    public required float Temperature { get; set; }
    public List<ChatResponse> Messages { get; set; } = [];
    public ResolvedWorkspace? Workspace { get; set; }
    public string? SessionWorkspaceOverride { get; set; }
    public HashSet<string> ActiveWorkspaceNames { get; set; } = [];

    public long SoftCompactThreshold = 35 * 1024;
    public int FreshMessagesCount = 8;

    public long MaxSequenceId() => Messages.Count > 0
        ? Messages.Max(m => m.AdditionalProperties?.GetValueOrDefault(Constants.SequenceIdKey, 0L) as long? ?? 0)
        : 0;
}