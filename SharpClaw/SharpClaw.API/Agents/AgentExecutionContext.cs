using Microsoft.Extensions.AI;

namespace SharpClaw.API.Agents;

public class AgentExecutionContext
{
    public required string DbConnectionString { get; set; }
    public required long AgentId { get; set; }
    public required ChatMessage SystemMessage { get; set; }
    public List<ChatResponse> Messages { get; set; } = [];

    public long SoftCompactThreshold = 4 * 1024;
    public int FreshMessagesCount = 8;
}