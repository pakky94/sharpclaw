namespace SharpClaw.API.Agents.Secrets;

public class Secret
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EncryptedValue { get; set; } = string.Empty;
    public string Scope { get; set; } = "global"; // global, user, agent
    public long? OwnerId { get; set; }
    public bool AllowBridge { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
