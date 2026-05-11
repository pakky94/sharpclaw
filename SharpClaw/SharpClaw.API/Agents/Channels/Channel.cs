namespace SharpClaw.API.Agents.Channels;

public class Channel
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long AgentId { get; set; }
    public string RoutingMode { get; set; } = "shared";
    public string Config { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
