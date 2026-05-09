using SharpClaw.API.Agents;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Agents.ScheduledJobs;

public class CronScheduler : BackgroundService
{
    private readonly ScheduledJobRepository _repo;
    private readonly Agent _agent;
    private readonly SessionStore _sessionStore;
    private readonly ILogger<CronScheduler> _logger;

    public CronScheduler(
        ScheduledJobRepository repo,
        Agent agent,
        SessionStore sessionStore,
        ILogger<CronScheduler> logger)
    {
        _repo = repo;
        _agent = agent;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("CronScheduler started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessDueJobs(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error processing scheduled jobs");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    private async Task ProcessDueJobs(CancellationToken ct)
    {
        var dueJobs = await _repo.GetDueJobs();

        foreach (var job in dueJobs)
        {
            ct.ThrowIfCancellationRequested();

            // Skip if previous run is still active
            if (job.LastSessionId is { } sid)
            {
                try
                {
                    var session = await _sessionStore.GetOrLoadSession(sid);
                    var status = session.Run?.Status;
                    if (status is AgentRunStatus.Pending
                              or AgentRunStatus.Waiting
                              or AgentRunStatus.Running)
                    {
                        _logger.LogDebug(
                            "Skipping job {JobName} ({JobId}): previous session {SessionId} is still {Status}",
                            job.Name, job.Id, sid, status);
                        continue;
                    }
                }
                catch (KeyNotFoundException)
                {
                    // Session not in memory/DB — safe to proceed
                }
            }

            _logger.LogInformation(
                "Firing job {JobName} ({JobId}) with agent {AgentId}",
                job.Name, job.Id, job.AgentId);

            try
            {
                var sessionId = await _agent.CreateSession(job.AgentId);
                await _agent.EnqueueMessage(sessionId, job.Prompt);

                var nextRunAt = ScheduledJobRepository.ComputeNextRunAt(
                    job.CronExpression, job.Timezone);
                await _repo.MarkFired(job.Id, sessionId, nextRunAt);

                _logger.LogInformation(
                    "Job {JobName} ({JobId}) fired: session {SessionId}, next run at {NextRun}",
                    job.Name, job.Id, sessionId, nextRunAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to fire job {JobName} ({JobId})", job.Name, job.Id);
            }
        }
    }
}
