using Microsoft.Extensions.AI;

namespace SharpClaw.API.Agents;

public class AgentExecutionContext
{
    public required string DbConnectionString { get; set; }
    public required long AgentId { get; set; }
    public List<ChatMessage> Messages { get; init; } = [];
}