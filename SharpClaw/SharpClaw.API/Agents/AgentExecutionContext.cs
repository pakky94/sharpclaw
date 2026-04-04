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
    public string? SystemMessage { get; set; }
    public List<ChatResponse> Messages { get; set; } = [];
    public ResolvedWorkspace? Workspace { get; set; }
    public string? SessionWorkspaceOverride { get; set; }
    public HashSet<string> ActiveWorkspaceNames { get; set; } = [];

    public long SoftCompactThreshold = 35 * 1024;
    public int FreshMessagesCount = 8;
}
