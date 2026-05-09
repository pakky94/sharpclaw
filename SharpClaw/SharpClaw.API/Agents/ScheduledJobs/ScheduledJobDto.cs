namespace SharpClaw.API.Agents.ScheduledJobs;

public record ScheduledJobDto(
    long Id,
    string Name,
    string CronExpression,
    string Timezone,
    string Prompt,
    long AgentId,
    bool Enabled,
    DateTimeOffset? LastRunAt,
    Guid? LastSessionId,
    DateTimeOffset NextRunAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record CreateScheduledJobRequest(
    string Name,
    string CronExpression,
    string? Timezone,
    string Prompt,
    long AgentId,
    bool? Enabled
);

public record UpdateScheduledJobRequest(
    string? Name,
    string? CronExpression,
    string? Timezone,
    string? Prompt,
    long? AgentId,
    bool? Enabled
);
