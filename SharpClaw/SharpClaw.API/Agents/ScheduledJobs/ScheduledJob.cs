namespace SharpClaw.API.Agents.ScheduledJobs;

public class ScheduledJob
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string Timezone { get; set; } = "Europe/Rome";
    public string Prompt { get; set; } = string.Empty;
    public long AgentId { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastSessionId { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
