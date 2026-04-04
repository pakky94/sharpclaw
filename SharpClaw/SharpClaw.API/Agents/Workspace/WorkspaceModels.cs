using System.Text.Json.Serialization;

namespace SharpClaw.API.Agents.Workspace;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspacePolicyMode
{
    [JsonStringEnumMemberName("unrestricted")]
    Unrestricted,
    [JsonStringEnumMemberName("true_unrestricted")]
    TrueUnrestricted,
    [JsonStringEnumMemberName("confirm_writes_and_exec")]
    ConfirmWritesAndExec,
    [JsonStringEnumMemberName("confirm_exec_only")]
    ConfirmExecOnly,
    [JsonStringEnumMemberName("read_only")]
    ReadOnly,
    [JsonStringEnumMemberName("disabled")]
    Disabled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalStatus
{
    [JsonStringEnumMemberName("pending")]
    Pending,
    [JsonStringEnumMemberName("approved")]
    Approved,
    [JsonStringEnumMemberName("rejected")]
    Rejected,
    [JsonStringEnumMemberName("expired")]
    Expired,
}

public enum ApprovalActionType
{
    Write,
    Delete,
    Move,
    Execute,
    DestructiveExecute,
}

public enum ApprovalRiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

public class Workspace
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string[] AllowlistPatterns { get; set; } = [];
    public string[] DenylistPatterns { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AgentWorkspaceAssignment
{
    public long Id { get; set; }
    public long AgentId { get; set; }
    public long WorkspaceId { get; set; }
    public WorkspacePolicyMode PolicyMode { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ResolvedWorkspace
{
    public Workspace Workspace { get; set; } = null!;
    public WorkspacePolicyMode PolicyMode { get; set; }
    public bool IsDefault { get; set; }

    public string Name => Workspace.Name;
    public string RootPath => Workspace.RootPath;
    public string[] AllowlistPatterns => Workspace.AllowlistPatterns;
    public string[] DenylistPatterns => Workspace.DenylistPatterns;
}

public class WorkspaceApprovalEvent
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public long AgentId { get; set; }
    public string ApprovalToken { get; set; } = string.Empty;
    public ApprovalActionType ActionType { get; set; }
    public string? TargetPath { get; set; }
    public string? CommandPreview { get; set; }
    public ApprovalRiskLevel RiskLevel { get; set; }
    public ApprovalStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class LcmFileArtifact
{
    public long Id { get; set; }
    public string FileId { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public string OriginTool { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public long ByteCount { get; set; }
    public string StorageKind { get; set; } = "filesystem";
    public string? FilesystemPath { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record ApprovalRequest(
    string ApprovalToken,
    ApprovalActionType ActionType,
    string? TargetPath,
    string? SourcePath,
    string? CommandPreview,
    ApprovalRiskLevel RiskLevel,
    string Description
);

public record ApprovalResponse(
    bool Approved,
    string? Reason
);

public class SessionActiveWorkspace
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public string WorkspaceName { get; set; } = string.Empty;
    public WorkspacePolicyMode PolicyMode { get; set; }
    public DateTime CreatedAt { get; set; }
}
