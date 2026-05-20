using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Backups;

namespace SharpClaw.API.Endpoints;

public static class BackupEndpoints
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/backups/config", async ([FromServices] BackupService service) =>
        {
            var config = await service.GetConfig();
            return Results.Ok(MapConfig(config));
        });

        app.MapPut("/backups/config", async (
            [FromBody] UpdateBackupConfigRequest request,
            [FromServices] BackupService service) =>
        {
            var existing = await service.GetConfig();

            var timezone = request.Timezone ?? existing.Timezone;
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                return Results.BadRequest(new { error = $"Unknown timezone '{timezone}'." });
            }

            var fullEveryN = request.FullEveryN ?? existing.FullEveryN;
            if (fullEveryN < 1)
                return Results.BadRequest(new { error = "fullEveryN must be >= 1." });

            var updated = await service.UpdateConfig(new BackupConfig
            {
                Id = existing.Id,
                Enabled = request.Enabled ?? existing.Enabled,
                Timezone = timezone,
                DailyTime = request.DailyTime ?? existing.DailyTime,
                FullEveryN = fullEveryN,
                RetentionDays = request.RetentionDays ?? existing.RetentionDays,
                RetentionFullChains = request.RetentionFullChains ?? existing.RetentionFullChains,
                StrictRestoreDefault = request.StrictRestoreDefault ?? existing.StrictRestoreDefault,
                StorageRoot = request.StorageRoot ?? existing.StorageRoot,
            });

            return Results.Ok(MapConfig(updated));
        });

        app.MapGet("/backups/runs", async (
            [FromQuery] int? limit,
            [FromServices] BackupService service) =>
        {
            var rows = await service.ListRuns(Math.Clamp(limit ?? 50, 1, 200));
            return Results.Ok(new { runs = rows.Select(MapRun) });
        });

        app.MapGet("/backups/artifacts", async (
            [FromQuery] int? limit,
            [FromServices] BackupService service) =>
        {
            var rows = await service.ListArtifacts(Math.Clamp(limit ?? 200, 1, 1000));
            return Results.Ok(new { artifacts = rows.Select(MapArtifact) });
        });

        app.MapPost("/backups/run", async (
            [FromBody] RunBackupRequest? request,
            [FromServices] BackupService service,
            CancellationToken ct) =>
        {
            var mode = string.IsNullOrWhiteSpace(request?.Mode) ? "auto" : request!.Mode!;
            if (mode is not ("auto" or "full" or "incremental"))
                return Results.BadRequest(new { error = "Mode must be one of: auto, full, incremental." });

            var run = await service.RunManualBackup(mode, ct);
            return Results.Ok(MapRun(run));
        });

        app.MapPost("/backups/restore", async (
            [FromBody] RestoreBackupRequest request,
            [FromServices] BackupService service,
            CancellationToken ct) =>
        {
            if (request.BackupId is null && string.IsNullOrWhiteSpace(request.ArtifactPath))
                return Results.BadRequest(new { error = "Provide either backupId or artifactPath." });

            await service.Restore(request.BackupId, request.ArtifactPath, request.Strict ?? true, ct);
            return Results.Ok(new { restored = true, backupId = request.BackupId, artifactPath = request.ArtifactPath });
        });

        app.MapDelete("/backups/runs/{backupId:guid}", async (
            [FromRoute] Guid backupId,
            [FromQuery] bool deleteArtifact,
            [FromServices] BackupService service) =>
        {
            await service.DeleteRun(backupId, deleteArtifact);
            return Results.Ok(new { deleted = true, backupId, deleteArtifact });
        });
    }

    private static BackupConfigDto MapConfig(BackupConfig config) => new(
        config.Id,
        config.Enabled,
        config.Timezone,
        config.DailyTime,
        config.FullEveryN,
        config.RetentionDays,
        config.RetentionFullChains,
        config.StrictRestoreDefault,
        config.StorageRoot,
        config.CreatedAt,
        config.UpdatedAt);

    private static BackupRunDto MapRun(BackupRun run) => new(
        run.Id,
        run.BackupId,
        run.BackupType == BackupType.Full ? "full" : "incremental",
        run.Status switch
        {
            BackupStatus.Running => "running",
            BackupStatus.Succeeded => "succeeded",
            BackupStatus.Failed => "failed",
            _ => "partial",
        },
        run.BaseFullBackupId,
        run.PreviousBackupId,
        run.WindowFromUtc,
        run.WindowToUtc,
        run.ArtifactPath,
        run.ErrorMessage,
        run.StartedAt,
        run.CompletedAt);

    private static BackupArtifactDto MapArtifact(BackupArtifact artifact) => new(
        artifact.BackupId,
        artifact.BackupType == BackupType.Full ? "full" : "incremental",
        artifact.BaseFullBackupId,
        artifact.PreviousBackupId,
        artifact.CreatedAtUtc,
        artifact.ArtifactPath);
}
