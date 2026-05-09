using Cronos;
using Dapper;
using Npgsql;
using SharpClaw.API.Agents.ScheduledJobs;

namespace SharpClaw.API.Database.Repositories;

public class ScheduledJobRepository(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("sharpclaw")!;

    public async Task<IReadOnlyList<ScheduledJob>> GetAll()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<ScheduledJobRow>(
            "select * from scheduled_jobs order by name");
        return rows.Select(r => r.ToModel()).ToArray();
    }

    public async Task<ScheduledJob?> GetById(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<ScheduledJobRow>(
            "select * from scheduled_jobs where id = @id", new { id });
        return row?.ToModel();
    }

    public async Task<ScheduledJob> Create(ScheduledJob job)
    {
        job.NextRunAt = ComputeNextRunAt(job.CronExpression, job.Timezone);

        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleAsync<ScheduledJobRow>(
            """
            insert into scheduled_jobs (name, cron_expression, timezone, prompt, agent_id, enabled, next_run_at)
            values (@Name, @CronExpression, @Timezone, @Prompt, @AgentId, @Enabled, @NextRunAt)
            returning *
            """,
            new
            {
                job.Name,
                job.CronExpression,
                job.Timezone,
                job.Prompt,
                job.AgentId,
                job.Enabled,
                NextRunAt = job.NextRunAt.ToUniversalTime(),
            });
        return row.ToModel();
    }

    public async Task<ScheduledJob?> Update(long id, ScheduledJob job)
    {
        job.NextRunAt = ComputeNextRunAt(job.CronExpression, job.Timezone);

        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<ScheduledJobRow>(
            """
            update scheduled_jobs
            set name = @Name,
                cron_expression = @CronExpression,
                timezone = @Timezone,
                prompt = @Prompt,
                agent_id = @AgentId,
                enabled = @Enabled,
                next_run_at = @NextRunAt,
                updated_at = now()
            where id = @Id
            returning *
            """,
            new
            {
                job.Id,
                job.Name,
                job.CronExpression,
                job.Timezone,
                job.Prompt,
                job.AgentId,
                job.Enabled,
                NextRunAt = job.NextRunAt.ToUniversalTime(),
            });
        return row?.ToModel();
    }

    public async Task<bool> Delete(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var deleted = await connection.ExecuteAsync(
            "delete from scheduled_jobs where id = @id", new { id });
        return deleted > 0;
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetDueJobs()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<ScheduledJobRow>(
            "select * from scheduled_jobs where enabled and next_run_at <= now()");
        return rows.Select(r => r.ToModel()).ToArray();
    }

    public async Task MarkFired(long id, Guid sessionId, DateTimeOffset nextRunAt)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            update scheduled_jobs
            set last_run_at = now(),
                last_session_id = @SessionId,
                next_run_at = @NextRunAt,
                updated_at = now()
            where id = @Id
            """,
            new
            {
                Id = id,
                SessionId = sessionId,
                NextRunAt = nextRunAt.ToUniversalTime(),
            });
    }

    public static DateTimeOffset ComputeNextRunAt(string cronExpression, string timezone)
    {
        var expression = CronExpression.Parse(cronExpression);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var now = DateTimeOffset.UtcNow;
        var next = expression.GetNextOccurrence(now, tz);
        return next ?? throw new InvalidOperationException(
            $"Cron expression '{cronExpression}' has no future occurrences.");
    }

    private sealed class ScheduledJobRow
    {
        public long id { get; set; }
        public string name { get; set; } = string.Empty;
        public string cron_expression { get; set; } = string.Empty;
        public string timezone { get; set; } = "Europe/Rome";
        public string prompt { get; set; } = string.Empty;
        public long agent_id { get; set; }
        public bool enabled { get; set; }
        public DateTimeOffset? last_run_at { get; set; }
        public Guid? last_session_id { get; set; }
        public DateTimeOffset next_run_at { get; set; }
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }

        public ScheduledJob ToModel() => new()
        {
            Id = id,
            Name = name,
            CronExpression = cron_expression,
            Timezone = timezone,
            Prompt = prompt,
            AgentId = agent_id,
            Enabled = enabled,
            LastRunAt = last_run_at,
            LastSessionId = last_session_id,
            NextRunAt = next_run_at,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }
}
