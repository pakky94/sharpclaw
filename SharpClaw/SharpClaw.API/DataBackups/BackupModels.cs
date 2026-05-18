namespace SharpClaw.API.Backups;

public enum BackupType
{
    Full,
    Incremental,
}

public enum BackupStatus
{
    Running,
    Succeeded,
    Failed,
    Partial,
}

public sealed class BackupConfig
{
    public long Id { get; set; }
    public bool Enabled { get; set; }
    public string Timezone { get; set; } = "Europe/Rome";
    public TimeOnly DailyTime { get; set; } = new(3, 0, 0);
    public int FullEveryN { get; set; } = 7;
    public int? RetentionDays { get; set; }
    public int? RetentionFullChains { get; set; }
    public bool StrictRestoreDefault { get; set; } = true;
    public string StorageRoot { get; set; } = "/data/backups";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BackupRun
{
    public long Id { get; set; }
    public Guid BackupId { get; set; }
    public BackupType BackupType { get; set; }
    public BackupStatus Status { get; set; }
    public Guid BaseFullBackupId { get; set; }
    public Guid? PreviousBackupId { get; set; }
    public DateTimeOffset? WindowFromUtc { get; set; }
    public DateTimeOffset WindowToUtc { get; set; }
    public string? ArtifactPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class BackupArtifact
{
    public Guid BackupId { get; set; }
    public BackupType BackupType { get; set; }
    public Guid BaseFullBackupId { get; set; }
    public Guid? PreviousBackupId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string ArtifactPath { get; set; } = string.Empty;
}

public sealed class BackupTableDefinition
{
    public required string Name { get; init; }
    public required string[] PrimaryKeyColumns { get; init; }
    public required string WatermarkColumn { get; init; }
    public required string Mode { get; init; }
    public string[] DeferredReferenceColumns { get; init; } = [];
}
