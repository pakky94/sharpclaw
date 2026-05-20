using Dapper;
using Npgsql;

namespace SharpClaw.API.Backups;

public class BackupRepository(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("sharpclaw")!;

    public async Task<BackupConfig> GetConfig()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleAsync<BackupConfigRow>(
            "select * from backup_configs order by id limit 1");
        return row.ToModel();
    }

    public async Task<BackupConfig> UpdateConfig(BackupConfig config)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleAsync<BackupConfigRow>(
            """
            update backup_configs
            set enabled = @Enabled,
                timezone = @Timezone,
                daily_time = @DailyTime,
                full_every_n = @FullEveryN,
                retention_days = @RetentionDays,
                retention_full_chains = @RetentionFullChains,
                strict_restore_default = @StrictRestoreDefault,
                storage_root = @StorageRoot,
                updated_at = now()
            where id = @Id
            returning *
            """,
            new
            {
                config.Id,
                config.Enabled,
                config.Timezone,
                DailyTime = config.DailyTime.ToTimeSpan(),
                config.FullEveryN,
                config.RetentionDays,
                config.RetentionFullChains,
                config.StrictRestoreDefault,
                config.StorageRoot,
            });

        return row.ToModel();
    }

    public async Task<BackupRun> CreateRun(BackupRun run)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleAsync<BackupRunRow>(
            """
            insert into backup_runs
                (backup_id, backup_type, status, base_full_backup_id, previous_backup_id, window_from_utc, window_to_utc)
            values
                (@BackupId, @BackupType, @Status, @BaseFullBackupId, @PreviousBackupId, @WindowFromUtc, @WindowToUtc)
            returning *
            """,
            new
            {
                run.BackupId,
                BackupType = ToDb(run.BackupType),
                Status = ToDb(run.Status),
                run.BaseFullBackupId,
                run.PreviousBackupId,
                run.WindowFromUtc,
                run.WindowToUtc,
            });

        return row.ToModel();
    }

    public async Task MarkRunSucceeded(Guid backupId, string artifactPath)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            update backup_runs
            set status = 'succeeded',
                artifact_path = @artifactPath,
                completed_at = now()
            where backup_id = @backupId
            """,
            new { backupId, artifactPath });
    }

    public async Task MarkRunFailed(Guid backupId, string errorMessage)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            update backup_runs
            set status = 'failed',
                error_message = @errorMessage,
                completed_at = now()
            where backup_id = @backupId
            """,
            new { backupId, errorMessage });
    }

    public async Task<BackupRun?> GetRunByBackupId(Guid backupId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<BackupRunRow>(
            "select * from backup_runs where backup_id = @backupId",
            new { backupId });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<BackupRun>> ListRuns(int limit = 50)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<BackupRunRow>(
            """
            select *
            from backup_runs
            order by started_at desc
            limit @limit
            """,
            new { limit });
        return rows.Select(r => r.ToModel()).ToArray();
    }

    public async Task<BackupRun?> GetLastSuccessfulRun()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<BackupRunRow>(
            """
            select *
            from backup_runs
            where status = 'succeeded'
            order by started_at desc
            limit 1
            """);
        return row?.ToModel();
    }

    public async Task<BackupRun?> GetLastSuccessfulFullRun()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<BackupRunRow>(
            """
            select *
            from backup_runs
            where status = 'succeeded' and backup_type = 'full'
            order by started_at desc
            limit 1
            """);
        return row?.ToModel();
    }

    public async Task<int> CountSuccessfulRunsSinceBase(Guid baseFullBackupId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            """
            select count(*)
            from backup_runs
            where status = 'succeeded'
              and base_full_backup_id = @baseFullBackupId
            """,
            new { baseFullBackupId });
    }

    public async Task<bool> HasSuccessfulRunOnLocalDate(string timezone, DateOnly localDate)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<bool>(
            """
            select exists(
                select 1
                from backup_runs
                where status = 'succeeded'
                  and (started_at at time zone @timezone)::date = @localDate
            )
            """,
            new { timezone, localDate = localDate.ToDateTime(TimeOnly.MinValue).Date });
    }

    public async Task<IReadOnlyList<BackupRun>> GetSuccessfulRunsForBase(Guid baseFullBackupId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<BackupRunRow>(
            """
            select *
            from backup_runs
            where status = 'succeeded' and base_full_backup_id = @baseFullBackupId
            order by started_at asc
            """,
            new { baseFullBackupId });
        return rows.Select(r => r.ToModel()).ToArray();
    }

    public async Task DeleteRun(Guid backupId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            "delete from backup_runs where backup_id = @backupId",
            new { backupId });
    }

    private static string ToDb(BackupType backupType) => backupType switch
    {
        BackupType.Full => "full",
        BackupType.Incremental => "incremental",
        _ => throw new ArgumentOutOfRangeException(nameof(backupType), backupType, null),
    };

    private static string ToDb(BackupStatus status) => status switch
    {
        BackupStatus.Running => "running",
        BackupStatus.Succeeded => "succeeded",
        BackupStatus.Failed => "failed",
        BackupStatus.Partial => "partial",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private sealed class BackupConfigRow
    {
        public long id { get; set; }
        public bool enabled { get; set; }
        public string timezone { get; set; } = "Europe/Rome";
        public TimeOnly daily_time { get; set; }
        public int full_every_n { get; set; }
        public int? retention_days { get; set; }
        public int? retention_full_chains { get; set; }
        public bool strict_restore_default { get; set; }
        public string storage_root { get; set; } = "/data/backups";
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }

        public BackupConfig ToModel() => new()
        {
            Id = id,
            Enabled = enabled,
            Timezone = timezone,
            DailyTime = daily_time,
            FullEveryN = full_every_n,
            RetentionDays = retention_days,
            RetentionFullChains = retention_full_chains,
            StrictRestoreDefault = strict_restore_default,
            StorageRoot = storage_root,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }

    private sealed class BackupRunRow
    {
        public long id { get; set; }
        public Guid backup_id { get; set; }
        public string backup_type { get; set; } = "full";
        public string status { get; set; } = "running";
        public Guid base_full_backup_id { get; set; }
        public Guid? previous_backup_id { get; set; }
        public DateTimeOffset? window_from_utc { get; set; }
        public DateTimeOffset window_to_utc { get; set; }
        public string? artifact_path { get; set; }
        public string? error_message { get; set; }
        public DateTimeOffset started_at { get; set; }
        public DateTimeOffset? completed_at { get; set; }

        public BackupRun ToModel() => new()
        {
            Id = id,
            BackupId = backup_id,
            BackupType = backup_type == "incremental" ? BackupType.Incremental : BackupType.Full,
            Status = status switch
            {
                "running" => BackupStatus.Running,
                "succeeded" => BackupStatus.Succeeded,
                "failed" => BackupStatus.Failed,
                _ => BackupStatus.Partial,
            },
            BaseFullBackupId = base_full_backup_id,
            PreviousBackupId = previous_backup_id,
            WindowFromUtc = window_from_utc,
            WindowToUtc = window_to_utc,
            ArtifactPath = artifact_path,
            ErrorMessage = error_message,
            StartedAt = started_at,
            CompletedAt = completed_at,
        };
    }
}
