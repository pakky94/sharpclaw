using System.Text.Json.Serialization;

namespace SharpClaw.BridgeClient;

public class BridgeRequest
{
    public string Type { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public Dictionary<string, object?> Args { get; set; } = [];
    public BridgePolicyContext? PolicyContext { get; set; }
}

public class BridgePolicyContext
{
    public string[] AllowlistPatterns { get; set; } = [];
    public string[] DenylistPatterns { get; set; } = [];
    public string PolicyMode { get; set; } = "confirm_writes_and_exec";
    public string RootPath { get; set; } = string.Empty;
}

public class BridgeResponse
{
    public string Type { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string Status { get; set; } = "ok";
    public object? Result { get; set; }
    public string? ErrorMessage { get; set; }
}

public class BridgeRegistration
{
    public string Type { get; set; } = "register";
    public string BridgeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = [];
    public string Os { get; set; } = string.Empty;
    public string Shell { get; set; } = string.Empty;
    public bool IsDevContainer { get; set; }
    public string? ContainerId { get; set; }
    public string? WorkspacePathInContainer { get; set; }
}

public class BridgeHeartbeat
{
    public string Type { get; set; } = "heartbeat";
    public string BridgeId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class FileEntry
{
    public string type { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
    public string path { get; set; } = string.Empty;
    public long? size { get; set; }
}
