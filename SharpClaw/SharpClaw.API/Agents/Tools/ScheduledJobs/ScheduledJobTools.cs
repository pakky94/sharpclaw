using Microsoft.Extensions.AI;
using SharpClaw.API.Database.Repositories;
using ScheduledJobModel = SharpClaw.API.Agents.ScheduledJobs.ScheduledJob;

namespace SharpClaw.API.Agents.Tools.ScheduledJobs;

public static class ScheduledJobTools
{
    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(ListJobs, "list_scheduled_jobs",
            "List all scheduled cron jobs."),
        AIFunctionFactory.Create(CreateJob, "create_scheduled_job",
            "Create a new scheduled cron job that will spawn an agent session on a schedule."),
        AIFunctionFactory.Create(UpdateJob, "update_scheduled_job",
            "Update an existing scheduled job. Only provide the fields you want to change."),
        AIFunctionFactory.Create(DeleteJob, "delete_scheduled_job",
            "Delete a scheduled job by ID."),
    ];

    private static async Task<object> ListJobs(IServiceProvider serviceProvider)
    {
        var repo = serviceProvider.GetRequiredService<ScheduledJobRepository>();
        var jobs = await repo.GetAll();
        return new
        {
            jobs = jobs.Select(MapToSummary),
        };
    }

    private static async Task<object> CreateJob(
        IServiceProvider serviceProvider,
        string name,
        string cron_expression,
        string prompt,
        long? agent_id = null,
        string? timezone = null)
    {
        var repo = serviceProvider.GetRequiredService<ScheduledJobRepository>();
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();

        var job = new ScheduledJobModel
        {
            Name = name,
            CronExpression = cron_expression,
            Timezone = timezone ?? "Europe/Rome",
            Prompt = prompt,
            AgentId = agent_id ?? ctx.AgentId,
            Enabled = true,
        };

        var created = await repo.Create(job);
        return MapToSummary(created);
    }

    private static async Task<object> UpdateJob(
        IServiceProvider serviceProvider,
        long id,
        string? name = null,
        string? cron_expression = null,
        string? prompt = null,
        bool? enabled = null,
        string? timezone = null)
    {
        var repo = serviceProvider.GetRequiredService<ScheduledJobRepository>();

        var existing = await repo.GetById(id);
        if (existing is null)
            return new { error = $"Job {id} not found." };

        var updated = new ScheduledJobModel
        {
            Id = id,
            Name = name ?? existing.Name,
            CronExpression = cron_expression ?? existing.CronExpression,
            Timezone = timezone ?? existing.Timezone,
            Prompt = prompt ?? existing.Prompt,
            AgentId = existing.AgentId,
            Enabled = enabled ?? existing.Enabled,
        };

        var result = await repo.Update(id, updated);
        return result is null
            ? new { error = $"Job {id} not found." }
            : MapToSummary(result);
    }

    private static async Task<object> DeleteJob(
        IServiceProvider serviceProvider,
        long id)
    {
        var repo = serviceProvider.GetRequiredService<ScheduledJobRepository>();
        var deleted = await repo.Delete(id);
        return new { deleted };
    }

    private static object MapToSummary(ScheduledJobModel job) => new
    {
        job.Id,
        job.Name,
        job.CronExpression,
        job.Timezone,
        job.Prompt,
        job.AgentId,
        job.Enabled,
        job.LastRunAt,
        job.LastSessionId,
        job.NextRunAt,
        job.CreatedAt,
        job.UpdatedAt,
    };
}
