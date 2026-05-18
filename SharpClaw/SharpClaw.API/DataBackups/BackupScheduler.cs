namespace SharpClaw.API.Backups;

public class BackupScheduler(BackupService backupService, ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BackupScheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var run = await backupService.RunScheduledBackupIfDue(stoppingToken);
                if (run is not null)
                {
                    logger.LogInformation(
                        "Scheduled backup completed. BackupId={BackupId}, Type={Type}",
                        run.BackupId,
                        run.BackupType);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled backup execution failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
