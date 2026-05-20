namespace SharpClaw.API.Backups;

public record BackupConfigDto(
    long Id,
    bool Enabled,
    string Timezone,
    TimeOnly DailyTime,
    int FullEveryN,
    int? RetentionDays,
    int? RetentionFullChains,
    bool StrictRestoreDefault,
    string StorageRoot,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record BackupRunDto(
    long Id,
    Guid BackupId,
    string BackupType,
    string Status,
    Guid BaseFullBackupId,
    Guid? PreviousBackupId,
    DateTimeOffset? WindowFromUtc,
    DateTimeOffset WindowToUtc,
    string? ArtifactPath,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public record BackupArtifactDto(
    Guid BackupId,
    string BackupType,
    Guid BaseFullBackupId,
    Guid? PreviousBackupId,
    DateTimeOffset CreatedAtUtc,
    string ArtifactPath);

public record UpdateBackupConfigRequest(
    bool? Enabled,
    string? Timezone,
    TimeOnly? DailyTime,
    int? FullEveryN,
    int? RetentionDays,
    int? RetentionFullChains,
    bool? StrictRestoreDefault,
    string? StorageRoot);

public record RunBackupRequest(string? Mode);

public record RestoreBackupRequest(Guid? BackupId, string? ArtifactPath, bool? Strict);
