namespace SharpClaw.API.Agents.Workspace;

public class BridgeRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public Guid SessionId { get; set; }
    public long AgentId { get; set; }
    public string WorkspaceName { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public Dictionary<string, object?> Args { get; set; } = [];
    public BridgePolicyContext PolicyContext { get; set; } = new();
    public BridgeLimits Limits { get; set; } = new();
}

public class BridgePolicyContext
{
    public string[] AllowlistPatterns { get; set; } = [];
    public string[] DenylistPatterns { get; set; } = [];
    public string PolicyMode { get; set; } = "confirm_writes_and_exec";
    public string RootPath { get; set; } = string.Empty;
}

public class BridgeLimits
{
    public int? TimeoutMs { get; set; }
    public int? MaxOutputBytes { get; set; }
}

public class BridgeResponse
{
    public string RequestId { get; set; } = string.Empty;
    public string Status { get; set; } = "ok"; // ok, error, timeout
    public object? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public BridgeExecutionMetadata? Metadata { get; set; }
}

public class BridgeExecutionMetadata
{
    public long DurationMs { get; set; }
    public long Bytes { get; set; }
    public bool Truncated { get; set; }
}

public class BridgeRegistration
{
    public string BridgeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = [];
    public string Os { get; set; } = string.Empty;
    public string? Shell { get; set; }
    public int? MaxTimeoutMs { get; set; }
    public Dictionary<string, object>? RootMappings { get; set; }
    public bool IsDevContainer { get; set; }
    public string? ContainerId { get; set; }
    public string? WorkspacePathInContainer { get; set; }
}

public class BridgeHeartbeat
{
    public string BridgeId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "online";
}
