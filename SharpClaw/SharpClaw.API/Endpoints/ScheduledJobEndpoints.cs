using Cronos;
using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Agents.ScheduledJobs;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Endpoints;

public static class ScheduledJobEndpoints
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/jobs", async (
            [FromServices] ScheduledJobRepository repo) =>
        {
            var jobs = await repo.GetAll();
            return Results.Ok(new
            {
                jobs = jobs.Select(MapToDto),
            });
        });

        app.MapPost("/jobs", async (
            [FromBody] CreateScheduledJobRequest request,
            [FromServices] ScheduledJobRepository repo,
            [FromServices] AgentsRepository agentsRepo) =>
        {
            // Validate cron expression
            try
            {
                CronExpression.Parse(request.CronExpression);
            }
            catch (CronFormatException)
            {
                return Results.BadRequest(new
                {
                    error = $"Invalid cron expression: '{request.CronExpression}'",
                });
            }

            // Validate timezone
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(request.Timezone ?? "Europe/Rome");
            }
            catch (TimeZoneNotFoundException)
            {
                return Results.BadRequest(new
                {
                    error = $"Unknown timezone: '{request.Timezone}'",
                });
            }

            // Validate agent exists
            var agent = await agentsRepo.GetAgent(request.AgentId);
            if (agent is null)
            {
                return Results.BadRequest(new
                {
                    error = $"Agent {request.AgentId} not found.",
                });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "Name is required." });
            }

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return Results.BadRequest(new { error = "Prompt is required." });
            }

            var job = new ScheduledJob
            {
                Name = request.Name,
                CronExpression = request.CronExpression,
                Timezone = request.Timezone ?? "Europe/Rome",
                Prompt = request.Prompt,
                AgentId = request.AgentId,
                Enabled = request.Enabled ?? true,
            };

            var created = await repo.Create(job);
            return Results.Ok(MapToDto(created));
        });

        app.MapPatch("/jobs/{id:long}", async (
            long id,
            [FromBody] UpdateScheduledJobRequest request,
            [FromServices] ScheduledJobRepository repo,
            [FromServices] AgentsRepository agentsRepo) =>
        {
            var existing = await repo.GetById(id);
            if (existing is null)
            {
                return Results.NotFound(new { error = $"Job {id} not found." });
            }

            if (request.CronExpression is not null)
            {
                try
                {
                    CronExpression.Parse(request.CronExpression);
                }
                catch (CronFormatException)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Invalid cron expression: '{request.CronExpression}'",
                    });
                }
            }

            if (request.Timezone is not null)
            {
                try
                {
                    TimeZoneInfo.FindSystemTimeZoneById(request.Timezone);
                }
                catch (TimeZoneNotFoundException)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Unknown timezone: '{request.Timezone}'",
                    });
                }
            }

            if (request.AgentId is { } agentId)
            {
                var agent = await agentsRepo.GetAgent(agentId);
                if (agent is null)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Agent {agentId} not found.",
                    });
                }
            }

            var updated = new ScheduledJob
            {
                Id = id,
                Name = request.Name ?? existing.Name,
                CronExpression = request.CronExpression ?? existing.CronExpression,
                Timezone = request.Timezone ?? existing.Timezone,
                Prompt = request.Prompt ?? existing.Prompt,
                AgentId = request.AgentId ?? existing.AgentId,
                Enabled = request.Enabled ?? existing.Enabled,
            };

            var result = await repo.Update(id, updated);
            if (result is null)
            {
                return Results.NotFound(new { error = $"Job {id} not found." });
            }

            return Results.Ok(MapToDto(result));
        });

        app.MapDelete("/jobs/{id:long}", async (
            long id,
            [FromServices] ScheduledJobRepository repo) =>
        {
            var deleted = await repo.Delete(id);
            if (!deleted)
            {
                return Results.NotFound(new { error = $"Job {id} not found." });
            }

            return Results.Ok(new { deleted = true });
        });
    }

    private static ScheduledJobDto MapToDto(ScheduledJob job) => new(
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
        job.UpdatedAt
    );
}
