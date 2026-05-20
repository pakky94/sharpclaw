namespace SharpClaw.BridgeClient;

public class Configuration
{
    public required string BridgeId { get; set; }
    public required string ServerUrl { get; set; }
    public string? DisplayName { get; set; }
}
