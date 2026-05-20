using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace SharpClaw.API.Backups;

public class BackupService(
    IConfiguration configuration,
    BackupRepository repository,
    ILogger<BackupService> logger)
{
    private const long BackupLockKey = 9142253319224011;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly IReadOnlyList<BackupTableDefinition> Tables =
    [
        new() { Name = "agents", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "sessions", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable", DeferredReferenceColumns = ["parent_session_id"] },
        new() { Name = "session_tasks", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "fragments", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable", DeferredReferenceColumns = ["parent_id"] },
        new() { Name = "fragment_shares", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "summaries", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable", DeferredReferenceColumns = ["parent_summary_id"] },
        new() { Name = "messages", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable", DeferredReferenceColumns = ["parent_summary_id"] },
        new() { Name = "conversation_history", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "workspaces", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "agent_workspace_assignments", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "workspace_approval_events", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "lcm_files", PrimaryKeyColumns = ["id"], WatermarkColumn = "created_at", Mode = "append_only" },
        new() { Name = "session_active_workspaces", PrimaryKeyColumns = ["id"], WatermarkColumn = "created_at", Mode = "append_only" },
        new() { Name = "bridge_clients", PrimaryKeyColumns = ["bridge_id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "bridge_execution_events", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "scheduled_jobs", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "channels", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "channel_sessions", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
        new() { Name = "secrets", PrimaryKeyColumns = ["id"], WatermarkColumn = "updated_at", Mode = "mutable" },
    ];

    private static readonly HashSet<string> TombstoneTables =
    [
        "fragments",
        "fragment_shares",
        "workspaces",
        "agent_workspace_assignments",
        "session_active_workspaces",
        "scheduled_jobs",
        "channels",
        "channel_sessions",
    ];

    private string ConnectionString => configuration.GetConnectionString("sharpclaw")!;

    public async Task<BackupRun> RunManualBackup(string mode, CancellationToken ct)
    {
        var requested = mode.Trim().ToLowerInvariant();
        var config = await repository.GetConfig();
        var type = await DecideBackupType(requested, config);
        return await RunBackup(type, config, ct);
    }

    public async Task<BackupRun?> RunScheduledBackupIfDue(CancellationToken ct)
    {
        var config = await repository.GetConfig();
        if (!config.Enabled)
            return null;

        var timezone = TimeZoneInfo.FindSystemTimeZoneById(config.Timezone);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timezone);
        var targetTime = config.DailyTime;

        if (nowLocal.TimeOfDay < targetTime.ToTimeSpan())
            return null;

        var localDate = DateOnly.FromDateTime(nowLocal.Date);
        if (await repository.HasSuccessfulRunOnLocalDate(config.Timezone, localDate))
            return null;

        var type = await DecideBackupType("auto", config);
        return await RunBackup(type, config, ct);
    }

    public Task<IReadOnlyList<BackupRun>> ListRuns(int limit = 50) => repository.ListRuns(limit);

    public Task<IReadOnlyList<BackupArtifact>> ListArtifacts(int limit = 200) => DiscoverArtifacts(limit, CancellationToken.None);

    public Task<BackupConfig> GetConfig() => repository.GetConfig();

    public Task<BackupConfig> UpdateConfig(BackupConfig config) => repository.UpdateConfig(config);

    public async Task DeleteRun(Guid backupId, bool deleteArtifact)
    {
        var run = await repository.GetRunByBackupId(backupId)
                  ?? throw new KeyNotFoundException($"Backup {backupId} not found.");

        await repository.DeleteRun(backupId);

        if (!deleteArtifact || string.IsNullOrWhiteSpace(run.ArtifactPath))
            return;

        if (!File.Exists(run.ArtifactPath))
            return;

        File.Delete(run.ArtifactPath);
        TryDeleteIfEmpty(Path.GetDirectoryName(run.ArtifactPath));
    }

    public async Task Restore(Guid? backupId, string? artifactPath, bool strict, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);

        if (!await TryAcquireBackupLock(connection, ct))
            throw new InvalidOperationException("Another backup or restore operation is currently running.");

        try
        {
            var chain = await ResolveRestoreChainForRequest(backupId, artifactPath, ct);
            var currentFingerprint = await ComputeSchemaFingerprint(connection, ct);
            var metadataMap = await LoadTableMetadata(connection, ct);

            foreach (var run in chain)
            {
                if (string.IsNullOrWhiteSpace(run.ArtifactPath) || !File.Exists(run.ArtifactPath))
                    throw new FileNotFoundException($"Backup artifact not found for {run.BackupId}.", run.ArtifactPath);

                await using var file = File.OpenRead(run.ArtifactPath);
                using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);

                var manifestEntry = archive.GetEntry("manifest.json")
                                    ?? throw new InvalidOperationException("Missing manifest.json in backup archive.");
                var manifest = await ReadManifest(manifestEntry, ct);

                if (strict && !string.Equals(manifest.SchemaFingerprint, currentFingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Schema fingerprint mismatch. Archive={manifest.SchemaFingerprint}, Current={currentFingerprint}");
                }

                await ValidateChecksums(archive, ct);

                await using var tx = await connection.BeginTransactionAsync(ct);

                foreach (var table in manifest.Tables)
                {
                    var tableDef = Tables.FirstOrDefault(t => string.Equals(t.Name, table.Name, StringComparison.Ordinal));
                    if (tableDef is null)
                    {
                        if (strict)
                            throw new InvalidOperationException($"Unsupported table in backup: {table.Name}");
                        continue;
                    }

                    if (!metadataMap.TryGetValue(tableDef.Name, out var metadata))
                    {
                        if (strict)
                            throw new InvalidOperationException($"Target database missing table: {tableDef.Name}");
                        continue;
                    }

                    var dataEntry = archive.GetEntry(table.File);
                    if (dataEntry is null)
                    {
                        if (strict)
                            throw new InvalidOperationException($"Missing table file: {table.File}");
                        continue;
                    }

                    await ApplyTableEntry(connection, tx, tableDef, metadata, dataEntry, ct);
                }

                if (manifest.BackupType == "incremental" && manifest.Tombstones is not null)
                {
                    var tombstoneEntry = archive.GetEntry(manifest.Tombstones.File);
                    if (tombstoneEntry is not null)
                        await ApplyTombstones(connection, tx, metadataMap, tombstoneEntry, ct);
                }

                await tx.CommitAsync(ct);
            }

            await ReseedOwnedSequences(connection, ct);
        }
        finally
        {
            await ReleaseBackupLock(connection, ct);
        }
    }

    private async Task<BackupType> DecideBackupType(string mode, BackupConfig config)
    {
        if (mode is "full")
            return BackupType.Full;

        var lastRun = await repository.GetLastSuccessfulRun();
        var lastFull = await repository.GetLastSuccessfulFullRun();

        if (mode is "incremental")
            return lastRun is null || lastFull is null ? BackupType.Full : BackupType.Incremental;

        if (lastRun is null || lastFull is null)
            return BackupType.Full;

        var countSinceBase = await repository.CountSuccessfulRunsSinceBase(lastRun.BaseFullBackupId);
        return countSinceBase % Math.Max(1, config.FullEveryN) == 0
            ? BackupType.Full
            : BackupType.Incremental;
    }

    private async Task<BackupRun> RunBackup(BackupType type, BackupConfig config, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);

        if (!await TryAcquireBackupLock(connection, ct))
            throw new InvalidOperationException("Another backup or restore operation is currently running.");

        var backupId = Guid.NewGuid();
        var nowUtc = DateTimeOffset.UtcNow;

        Guid baseFullBackupId;
        Guid? previousBackupId;
        DateTimeOffset? windowFromUtc;

        var lastSuccessfulRun = await repository.GetLastSuccessfulRun();

        if (type == BackupType.Full || lastSuccessfulRun is null)
        {
            type = BackupType.Full;
            baseFullBackupId = backupId;
            previousBackupId = lastSuccessfulRun?.BackupId;
            windowFromUtc = null;
        }
        else
        {
            baseFullBackupId = lastSuccessfulRun.BaseFullBackupId;
            previousBackupId = lastSuccessfulRun.BackupId;
            windowFromUtc = lastSuccessfulRun.WindowToUtc;
        }

        await repository.CreateRun(new BackupRun
        {
            BackupId = backupId,
            BackupType = type,
            Status = BackupStatus.Running,
            BaseFullBackupId = baseFullBackupId,
            PreviousBackupId = previousBackupId,
            WindowFromUtc = windowFromUtc,
            WindowToUtc = nowUtc,
        });

        try
        {
            Directory.CreateDirectory(config.StorageRoot);
            var backupDirectory = Path.Combine(config.StorageRoot, backupId.ToString("D"));
            Directory.CreateDirectory(backupDirectory);
            var sortableTimestamp = nowUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
            var typePrefix = type == BackupType.Full ? "full" : "inc";
            var artifactPath = Path.Combine(backupDirectory, $"{sortableTimestamp}_{typePrefix}_{backupId}.scbackup.zip");
            var tempArtifactPath = artifactPath + ".tmp";

            if (File.Exists(tempArtifactPath))
                File.Delete(tempArtifactPath);

            await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            var schemaFingerprint = await ComputeSchemaFingerprint(connection, ct, tx);

            var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
            var tableManifests = new List<BackupTableManifest>();
            BackupTombstonesManifest? tombstonesManifest = null;

            await using (var output = File.Create(tempArtifactPath))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var table in Tables)
                {
                    var path = type == BackupType.Full
                        ? $"full/{table.Name}.ndjson"
                        : $"inc/{table.Name}.ndjson";

                    var written = await WriteTableEntry(
                        connection,
                        tx,
                        archive,
                        table,
                        path,
                        type == BackupType.Full ? null : windowFromUtc,
                        nowUtc,
                        ct);

                    checksums[path] = written.Sha256;
                    tableManifests.Add(new BackupTableManifest
                    {
                        Name = table.Name,
                        Mode = table.Mode,
                        PrimaryKey = table.PrimaryKeyColumns,
                        File = path,
                        RowCount = written.RowCount,
                        Sha256 = written.Sha256,
                    });
                }

                if (type == BackupType.Incremental)
                {
                    var tombstonesWritten = await WriteTombstonesEntry(
                        connection,
                        tx,
                        archive,
                        "inc/tombstones.ndjson",
                        windowFromUtc,
                        nowUtc,
                        ct);

                    checksums[tombstonesWritten.Path] = tombstonesWritten.Sha256;
                    tombstonesManifest = new BackupTombstonesManifest
                    {
                        File = tombstonesWritten.Path,
                        RowCount = tombstonesWritten.RowCount,
                        Sha256 = tombstonesWritten.Sha256,
                    };
                }

                var manifest = new BackupManifest
                {
                    FormatVersion = 1,
                    BackupId = backupId,
                    BackupType = type == BackupType.Full ? "full" : "incremental",
                    CreatedAtUtc = nowUtc,
                    AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    SchemaFingerprint = schemaFingerprint,
                    Source = new BackupSourceManifest
                    {
                        DbEngine = "postgresql",
                        DbEngineVersion = connection.PostgreSqlVersion.ToString(),
                    },
                    Chain = new BackupChainManifest
                    {
                        BaseFullBackupId = baseFullBackupId,
                        PreviousBackupId = previousBackupId,
                        WindowFromUtc = windowFromUtc,
                        WindowToUtc = nowUtc,
                    },
                    Tables = tableManifests,
                    Tombstones = tombstonesManifest,
                };

                var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
                var manifestChecksum = await WriteTextEntry(archive, "manifest.json", manifestJson, ct);
                checksums["manifest.json"] = manifestChecksum;

                var checksumsText = BuildChecksumsText(checksums);
                await WriteTextEntry(archive, "checksums.sha256", checksumsText, ct);
            }

            await tx.CommitAsync(ct);

            if (File.Exists(artifactPath))
                File.Delete(artifactPath);
            File.Move(tempArtifactPath, artifactPath);

            await repository.MarkRunSucceeded(backupId, artifactPath);
            logger.LogInformation("Backup {BackupId} completed: {Path}", backupId, artifactPath);
            return (await repository.GetRunByBackupId(backupId))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup {BackupId} failed", backupId);
            await repository.MarkRunFailed(backupId, ex.Message);
            throw;
        }
        finally
        {
            await ReleaseBackupLock(connection, ct);
        }
    }

    private async Task<IReadOnlyList<BackupRun>> ResolveRestoreChainForRequest(Guid? backupId, string? artifactPath, CancellationToken ct)
    {
        if (backupId is not null)
        {
            var targetRun = await repository.GetRunByBackupId(backupId.Value)
                            ?? throw new KeyNotFoundException($"Backup {backupId} not found.");

            if (targetRun.Status != BackupStatus.Succeeded)
                throw new InvalidOperationException($"Backup {backupId} is not in succeeded state.");

            return await ResolveRestoreChain(targetRun);
        }

        if (string.IsNullOrWhiteSpace(artifactPath))
            throw new InvalidOperationException("Either backupId or artifactPath is required.");

        return await ResolveRestoreChainFromArtifactPath(artifactPath, ct);
    }

    private async Task<IReadOnlyList<BackupRun>> ResolveRestoreChain(BackupRun target)
    {
        if (target.BackupType == BackupType.Full)
            return [target];

        var runs = await repository.GetSuccessfulRunsForBase(target.BaseFullBackupId);
        var byId = runs.ToDictionary(r => r.BackupId);

        var ordered = new List<BackupRun>();
        var cursor = target;

        while (true)
        {
            ordered.Add(cursor);
            if (cursor.PreviousBackupId is null)
                break;

            if (!byId.TryGetValue(cursor.PreviousBackupId.Value, out var previous))
                throw new InvalidOperationException(
                    $"Backup chain is broken. Missing previous backup {cursor.PreviousBackupId.Value}.");

            cursor = previous;
        }

        ordered.Reverse();

        if (ordered.Count == 0 || ordered[0].BackupType != BackupType.Full)
            throw new InvalidOperationException("Restore chain must begin with a full backup.");

        return ordered;
    }

    private async Task<IReadOnlyList<BackupRun>> ResolveRestoreChainFromArtifactPath(string artifactPath, CancellationToken ct)
    {
        if (!File.Exists(artifactPath))
            throw new FileNotFoundException($"Backup artifact not found at '{artifactPath}'.", artifactPath);

        var discovered = await DiscoverArtifacts(limit: 5000, ct);
        var index = discovered.ToDictionary(a => a.BackupId, a => a);

        var target = discovered.FirstOrDefault(a =>
            string.Equals(a.ArtifactPath, artifactPath, StringComparison.OrdinalIgnoreCase));

        if (target is null)
            throw new InvalidOperationException($"Artifact '{artifactPath}' is not readable or has invalid manifest.");

        var ordered = new List<BackupRun>();
        var cursor = target;

        while (true)
        {
            ordered.Add(ToRestoreRun(cursor));

            if (cursor.PreviousBackupId is null)
                break;

            if (!index.TryGetValue(cursor.PreviousBackupId.Value, out var previous))
            {
                throw new InvalidOperationException(
                    $"Backup chain is broken. Missing artifact for previous backup {cursor.PreviousBackupId.Value}.");
            }

            cursor = previous;
        }

        ordered.Reverse();
        if (ordered.Count == 0 || ordered[0].BackupType != BackupType.Full)
            throw new InvalidOperationException("Restore chain must begin with a full backup.");

        return ordered;
    }

    private static BackupRun ToRestoreRun(BackupArtifact artifact) => new()
    {
        Id = 0,
        BackupId = artifact.BackupId,
        BackupType = artifact.BackupType,
        Status = BackupStatus.Succeeded,
        BaseFullBackupId = artifact.BaseFullBackupId,
        PreviousBackupId = artifact.PreviousBackupId,
        WindowFromUtc = null,
        WindowToUtc = artifact.CreatedAtUtc,
        ArtifactPath = artifact.ArtifactPath,
        ErrorMessage = null,
        StartedAt = artifact.CreatedAtUtc,
        CompletedAt = artifact.CreatedAtUtc,
    };

    private async Task<IReadOnlyList<BackupArtifact>> DiscoverArtifacts(int limit, CancellationToken ct)
    {
        var config = await repository.GetConfig();
        if (string.IsNullOrWhiteSpace(config.StorageRoot) || !Directory.Exists(config.StorageRoot))
            return [];

        var artifacts = new List<BackupArtifact>();
        foreach (var file in Directory.EnumerateFiles(config.StorageRoot, "*.scbackup.zip", SearchOption.AllDirectories))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                var manifestEntry = archive.GetEntry("manifest.json");
                if (manifestEntry is null)
                    continue;

                var manifest = await ReadManifest(manifestEntry, ct);
                artifacts.Add(new BackupArtifact
                {
                    BackupId = manifest.BackupId,
                    BackupType = manifest.BackupType == "incremental" ? BackupType.Incremental : BackupType.Full,
                    BaseFullBackupId = manifest.Chain.BaseFullBackupId,
                    PreviousBackupId = manifest.Chain.PreviousBackupId,
                    CreatedAtUtc = manifest.CreatedAtUtc,
                    ArtifactPath = file,
                });
            }
            catch
            {
                // Skip invalid or unreadable artifacts.
            }
        }

        return artifacts
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(limit)
            .ToArray();
    }

    private static void TryDeleteIfEmpty(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return;

        if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
            return;

        Directory.Delete(directoryPath);
    }

    private static async Task<bool> TryAcquireBackupLock(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select pg_try_advisory_lock(@k)", connection);
        cmd.Parameters.AddWithValue("k", BackupLockKey);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task ReleaseBackupLock(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select pg_advisory_unlock(@k)", connection);
        cmd.Parameters.AddWithValue("k", BackupLockKey);
        await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<BackupManifest> ReadManifest(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        var json = await reader.ReadToEndAsync(ct);
        return JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions)
               ?? throw new InvalidOperationException("Invalid manifest format.");
    }

    private static async Task ValidateChecksums(ZipArchive archive, CancellationToken ct)
    {
        var checksumsEntry = archive.GetEntry("checksums.sha256")
                            ?? throw new InvalidOperationException("Missing checksums.sha256 in backup archive.");

        Dictionary<string, string> expected;
        await using (var stream = checksumsEntry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false))
        {
            expected = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var split = line.Split("  ", 2, StringSplitOptions.None);
                if (split.Length == 2)
                    expected[split[1]] = split[0];
            }
        }

        foreach (var kv in expected)
        {
            var entry = archive.GetEntry(kv.Key)
                        ?? throw new InvalidOperationException($"Missing checksummed entry: {kv.Key}");
            var actual = await ComputeEntrySha256(entry, ct);
            if (!string.Equals(actual, kv.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Checksum mismatch for {kv.Key}");
        }
    }

    private static async Task<string> ComputeEntrySha256(ZipArchiveEntry entry, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = entry.Open();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string BuildChecksumsText(IReadOnlyDictionary<string, string> checksums)
    {
        var sb = new StringBuilder();
        foreach (var item in checksums.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(item.Value).Append("  ").Append(item.Key).Append('\n');
        return sb.ToString();
    }

    private static async Task<string> WriteTextEntry(ZipArchive archive, string path, string content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static async Task<(string Path, long RowCount, string Sha256)> WriteTombstonesEntry(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        ZipArchive archive,
        string path,
        DateTimeOffset? from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n",
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var sql =
            """
            select jsonb_build_object('tableName', table_name, 'pk', pk, 'deletedAt', deleted_at)::text
            from backup_tombstones
            where deleted_at > @from and deleted_at <= @to
            order by deleted_at, id
            """;

        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("from", from ?? DateTimeOffset.MinValue);
        cmd.Parameters.AddWithValue("to", to);

        long count = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var line = reader.GetString(0);
            await WriteLineWithHash(writer, hash, line);
            count++;
        }

        await writer.FlushAsync(ct);
        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return (path, count, sha);
    }

    private static async Task<(long RowCount, string Sha256)> WriteTableEntry(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        ZipArchive archive,
        BackupTableDefinition table,
        string path,
        DateTimeOffset? from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n",
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var tableName = QuoteIdentifier(table.Name);
        var pkOrder = string.Join(", ", table.PrimaryKeyColumns.Select(c => $"t.{QuoteIdentifier(c)}"));

        string sql;
        if (from is null)
        {
            sql = $"""
                   select row_to_json(t)::text
                   from {tableName} t
                   order by {pkOrder}
                   """;
        }
        else
        {
            sql = $"""
                   select row_to_json(t)::text
                   from {tableName} t
                   where t.{QuoteIdentifier(table.WatermarkColumn)} > @from
                     and t.{QuoteIdentifier(table.WatermarkColumn)} <= @to
                   order by t.{QuoteIdentifier(table.WatermarkColumn)}, {pkOrder}
                   """;
        }

        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        if (from is not null)
        {
            cmd.Parameters.AddWithValue("from", from.Value);
            cmd.Parameters.AddWithValue("to", to);
        }

        long count = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var line = reader.GetString(0);
            await WriteLineWithHash(writer, hash, line);
            count++;
        }

        await writer.FlushAsync(ct);
        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return (count, sha);
    }

    private static async Task WriteLineWithHash(StreamWriter writer, IncrementalHash hash, string line)
    {
        await writer.WriteLineAsync(line);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        hash.AppendData(bytes);
    }

    private async Task ReseedOwnedSequences(NpgsqlConnection connection, CancellationToken ct)
    {
        var tableNames = Tables.Select(t => t.Name).ToArray();

        const string sequenceSql =
            """
            select
                t.relname as table_name,
                a.attname as column_name,
                format('%I.%I', sn.nspname, s.relname) as sequence_name
            from pg_class s
            join pg_namespace sn on sn.oid = s.relnamespace
            join pg_depend d on d.objid = s.oid
            join pg_class t on t.oid = d.refobjid
            join pg_namespace tn on tn.oid = t.relnamespace
            join pg_attribute a on a.attrelid = t.oid and a.attnum = d.refobjsubid
            where s.relkind = 'S'
              and d.deptype = 'a'
              and tn.nspname = 'public'
              and t.relname = any(@table_names)
            order by t.relname, a.attname
            """;

        var sequences = new List<(string TableName, string ColumnName, string SequenceName)>();
        await using (var listCmd = new NpgsqlCommand(sequenceSql, connection))
        {
            listCmd.Parameters.AddWithValue("table_names", tableNames);
            await using var reader = await listCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sequences.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var (tableName, columnName, sequenceName) in sequences)
        {
            var quotedTable = $"{QuoteIdentifier("public")}.{QuoteIdentifier(tableName)}";
            var quotedColumn = QuoteIdentifier(columnName);

            var maxSql = $"select max({quotedColumn})::bigint from {quotedTable}";
            await using var maxCmd = new NpgsqlCommand(maxSql, connection);
            var maxValueObj = await maxCmd.ExecuteScalarAsync(ct);

            long setValue;
            bool isCalled;
            if (maxValueObj is null || maxValueObj is DBNull)
            {
                setValue = 1;
                isCalled = false;
            }
            else
            {
                setValue = Convert.ToInt64(maxValueObj);
                isCalled = true;
            }

            const string reseedSql = "select setval(@sequence_name::regclass, @value, @is_called)";
            await using var reseedCmd = new NpgsqlCommand(reseedSql, connection);
            reseedCmd.Parameters.AddWithValue("sequence_name", sequenceName);
            reseedCmd.Parameters.AddWithValue("value", setValue);
            reseedCmd.Parameters.AddWithValue("is_called", isCalled);
            await reseedCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<Dictionary<string, TableMetadata>> LoadTableMetadata(NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<string, TableMetadata>(StringComparer.Ordinal);

        foreach (var table in Tables)
        {
            const string sql =
                """
                select column_name, udt_name
                from information_schema.columns
                where table_schema = 'public' and table_name = @table
                order by ordinal_position
                """;

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("table", table.Name);
            var columns = new List<TableColumn>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                columns.Add(new TableColumn
                {
                    Name = reader.GetString(0),
                    UdtName = reader.GetString(1),
                });
            }

            if (columns.Count == 0)
                continue;

            result[table.Name] = new TableMetadata
            {
                Name = table.Name,
                Columns = columns,
            };
        }

        return result;
    }

    private async Task ApplyTableEntry(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        BackupTableDefinition table,
        TableMetadata metadata,
        ZipArchiveEntry entry,
        CancellationToken ct)
    {
        var deferred = table.DeferredReferenceColumns
            .Where(c => metadata.Columns.Any(mc => string.Equals(mc.Name, c, StringComparison.Ordinal)))
            .ToArray();

        var insertColumns = metadata.Columns
            .Select(c => c.Name)
            .Where(c => !deferred.Contains(c, StringComparer.Ordinal))
            .ToArray();

        var updateColumns = insertColumns
            .Where(c => !table.PrimaryKeyColumns.Contains(c, StringComparer.Ordinal))
            .ToArray();

        var upsertSql = BuildUpsertSql(table.Name, insertColumns, table.PrimaryKeyColumns, updateColumns, deferred);
        await using var upsertCmd = new NpgsqlCommand(upsertSql, connection, tx);
        var docParameter = upsertCmd.Parameters.Add("doc", NpgsqlDbType.Jsonb);

        await using (var stream = entry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false))
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                docParameter.Value = line;
                await upsertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (deferred.Length == 0)
            return;

        foreach (var deferredColumn in deferred)
        {
            var updateSql = BuildDeferredUpdateSql(table.Name, table.PrimaryKeyColumns, deferredColumn);
            await using var deferredCmd = new NpgsqlCommand(updateSql, connection, tx);

            var deferredParam = deferredCmd.Parameters.Add("value", GetNpgsqlType(metadata, deferredColumn));
            var pkParams = table.PrimaryKeyColumns
                .Select(pk =>
                {
                    var p = deferredCmd.Parameters.Add($"pk_{pk}", GetNpgsqlType(metadata, pk));
                    return (pk, p);
                })
                .ToArray();

            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty(deferredColumn, out var valueElement))
                    continue;

                deferredParam.Value = ConvertJsonToDbValue(valueElement, metadata.Columns.First(c => c.Name == deferredColumn).UdtName) ?? DBNull.Value;

                foreach (var (pk, parameter) in pkParams)
                {
                    if (!root.TryGetProperty(pk, out var pkElement))
                        throw new InvalidOperationException($"Row is missing primary key field '{pk}' for table '{table.Name}'.");

                    parameter.Value = ConvertJsonToDbValue(pkElement, metadata.Columns.First(c => c.Name == pk).UdtName)
                                      ?? throw new InvalidOperationException($"Primary key '{pk}' cannot be null for table '{table.Name}'.");
                }

                await deferredCmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private async Task ApplyTombstones(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        IReadOnlyDictionary<string, TableMetadata> metadata,
        ZipArchiveEntry entry,
        CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tableName", out var tableNameEl))
                continue;

            var tableName = tableNameEl.GetString();
            if (string.IsNullOrWhiteSpace(tableName) || !TombstoneTables.Contains(tableName))
                continue;

            if (!metadata.TryGetValue(tableName, out var tableMetadata))
                continue;

            if (!root.TryGetProperty("pk", out var pkElement) || pkElement.ValueKind != JsonValueKind.Object)
                continue;

            if (!pkElement.TryGetProperty("id", out var idElement))
                continue;

            var idColumn = tableMetadata.Columns.FirstOrDefault(c => c.Name == "id");
            if (idColumn is null)
                continue;

            var idValue = ConvertJsonToDbValue(idElement, idColumn.UdtName);
            if (idValue is null)
                continue;

            var sql = $"delete from {QuoteIdentifier(tableName)} where id = @id";
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            cmd.Parameters.AddWithValue("id", idValue);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string BuildUpsertSql(
        string table,
        string[] insertColumns,
        string[] pkColumns,
        string[] updateColumns,
        string[] deferredColumns)
    {
        var tableName = QuoteIdentifier(table);
        var insertColumnsSql = string.Join(", ", insertColumns.Select(QuoteIdentifier));

        var stripSql = deferredColumns.Length == 0
            ? "@doc::jsonb"
            : string.Join(' ', new[] { "@doc::jsonb" }.Concat(deferredColumns.Select(c => $"- '{c}'")));

        var conflictSql = updateColumns.Length == 0
            ? "do nothing"
            : "do update set " + string.Join(", ", updateColumns.Select(c => $"{QuoteIdentifier(c)} = excluded.{QuoteIdentifier(c)}"));

        return $"""
                insert into {tableName} ({insertColumnsSql})
                select {insertColumnsSql}
                from jsonb_populate_record(null::{tableName}, {stripSql})
                on conflict ({string.Join(", ", pkColumns.Select(QuoteIdentifier))})
                {conflictSql};
                """;
    }

    private static string BuildDeferredUpdateSql(string table, string[] pkColumns, string deferredColumn)
    {
        var where = string.Join(" and ", pkColumns.Select(pk => $"{QuoteIdentifier(pk)} = @pk_{pk}"));
        return $"""
                update {QuoteIdentifier(table)}
                set {QuoteIdentifier(deferredColumn)} = @value
                where {where}
                """;
    }

    private static NpgsqlDbType GetNpgsqlType(TableMetadata metadata, string columnName)
    {
        var udt = metadata.Columns.First(c => c.Name == columnName).UdtName;
        return udt switch
        {
            "uuid" => NpgsqlDbType.Uuid,
            "int8" => NpgsqlDbType.Bigint,
            "int4" => NpgsqlDbType.Integer,
            "bool" => NpgsqlDbType.Boolean,
            "json" or "jsonb" => NpgsqlDbType.Jsonb,
            "timestamptz" => NpgsqlDbType.TimestampTz,
            "timestamp" => NpgsqlDbType.Timestamp,
            "date" => NpgsqlDbType.Date,
            "time" => NpgsqlDbType.Time,
            _ => NpgsqlDbType.Text,
        };
    }

    private static object? ConvertJsonToDbValue(JsonElement element, string udtName)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        return udtName switch
        {
            "uuid" => Guid.Parse(element.GetString()!),
            "int8" => element.GetInt64(),
            "int4" => element.GetInt32(),
            "bool" => element.GetBoolean(),
            "json" or "jsonb" => element.GetRawText(),
            "timestamptz" => DateTimeOffset.Parse(element.GetString()!),
            "timestamp" => DateTime.Parse(element.GetString()!),
            "date" => DateOnly.Parse(element.GetString()!),
            "time" => TimeOnly.Parse(element.GetString()!),
            _ => element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => element.GetRawText(),
            },
        };
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    private async Task<string> ComputeSchemaFingerprint(
        NpgsqlConnection connection,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        var sb = new StringBuilder();

        foreach (var table in Tables)
        {
            await using var cmd = new NpgsqlCommand(
                """
                select column_name, udt_name, is_nullable, ordinal_position
                from information_schema.columns
                where table_schema = 'public' and table_name = @table
                order by ordinal_position
                """,
                connection,
                tx);
            cmd.Parameters.AddWithValue("table", table.Name);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var line = $"{table.Name}|{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetInt32(3)}";
                sb.AppendLine(line);
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed class TableMetadata
    {
        public required string Name { get; init; }
        public required List<TableColumn> Columns { get; init; }
    }

    private sealed class TableColumn
    {
        public required string Name { get; init; }
        public required string UdtName { get; init; }
    }

    private sealed class BackupManifest
    {
        public int FormatVersion { get; set; }
        public Guid BackupId { get; set; }
        public string BackupType { get; set; } = "full";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string AppVersion { get; set; } = "unknown";
        public string SchemaFingerprint { get; set; } = string.Empty;
        public BackupSourceManifest Source { get; set; } = new();
        public BackupChainManifest Chain { get; set; } = new();
        public List<BackupTableManifest> Tables { get; set; } = [];
        public BackupTombstonesManifest? Tombstones { get; set; }
    }

    private sealed class BackupSourceManifest
    {
        public string DbEngine { get; set; } = "postgresql";
        public string DbEngineVersion { get; set; } = string.Empty;
    }

    private sealed class BackupChainManifest
    {
        public Guid BaseFullBackupId { get; set; }
        public Guid? PreviousBackupId { get; set; }
        public DateTimeOffset? WindowFromUtc { get; set; }
        public DateTimeOffset WindowToUtc { get; set; }
    }

    private sealed class BackupTableManifest
    {
        public string Name { get; set; } = string.Empty;
        public string Mode { get; set; } = "mutable";
        public string[] PrimaryKey { get; set; } = [];
        public string File { get; set; } = string.Empty;
        public long RowCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class BackupTombstonesManifest
    {
        public string File { get; set; } = "inc/tombstones.ndjson";
        public long RowCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
