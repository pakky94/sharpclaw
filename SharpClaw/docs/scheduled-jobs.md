# Scheduled Jobs (Cron) — Implementation Plan

## Overview

Add cron-based scheduled jobs to SharpClaw. Users (and agents) can create jobs that fire on a schedule, spawning a new agent session with a predefined prompt. The agent can then act on that prompt (e.g., check calendar, summarize emails, run reports).

---

## 1. Database

### 1.1 Migration: `scheduled_jobs` table

```sql
CREATE TABLE scheduled_jobs (
    id              BIGSERIAL PRIMARY KEY,
    name            TEXT NOT NULL,
    cron_expression TEXT NOT NULL,
    timezone        TEXT NOT NULL DEFAULT 'Europe/Rome',
    prompt          TEXT NOT NULL,
    agent_id        BIGINT NOT NULL REFERENCES agents(id),
    enabled         BOOLEAN NOT NULL DEFAULT true,
    last_run_at     TIMESTAMPTZ,
    last_session_id UUID,
    next_run_at     TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

**Design notes:**
- `cron_expression` — standard 5-field cron (minute hour day month weekday). Validated on write.
- `timezone` — IANA timezone string (e.g. `Europe/Rome`). Cron is evaluated in this timezone. `next_run_at` is stored as UTC internally.
- `last_session_id` — used to detect overlapping runs. Before firing, check if this session is still active; if so, skip.
- `next_run_at` — pre-computed UTC timestamp of the next fire. Updated after each fire. On server restart, any job with `next_run_at` in the past fires on the next poll tick (no missed-fire catch-up needed).

### 1.2 Migration file

Place in `SharpClaw.API/Database/Migrations/` following existing conventions. Use the same migration runner used by other tables.

---

## 2. Backend: Model & Repository

### 2.1 `ScheduledJob.cs` — Model

**Location:** `SharpClaw.API/Agents/ScheduledJobs/ScheduledJob.cs`

```csharp
public class ScheduledJob
{
    public long Id { get; set; }
    public string Name { get; set; }
    public string CronExpression { get; set; }
    public string Timezone { get; set; }
    public string Prompt { get; set; }
    public long AgentId { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastSessionId { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

### 2.2 `ScheduledJobRepository.cs` — Data Access

**Location:** `SharpClaw.API/Database/Repositories/ScheduledJobRepository.cs`

Methods:

| Method | SQL | Notes |
|---|---|---|
| `GetAll()` | `SELECT * FROM scheduled_jobs ORDER BY name` | For UI listing |
| `GetById(id)` | `SELECT * WHERE id = @id` | |
| `Create(job)` | `INSERT ... RETURNING *` | Computes `next_run_at` from cron + timezone |
| `Update(job)` | `UPDATE ... WHERE id = @id` | Recomputes `next_run_at` if cron/timezone changed |
| `Delete(id)` | `DELETE WHERE id = @id` | |
| `GetDueJobs()` | `SELECT * WHERE enabled AND next_run_at <= now()` | For the scheduler |
| `MarkFired(id, sessionId, nextRunAt)` | `UPDATE SET last_run_at=now(), last_session_id=@sid, next_run_at=@next` | Atomic update after firing |

**Key detail:** `next_run_at` is always pre-computed using Cronos:
```csharp
CronExpression.Parse(job.CronExpression)
    .GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(job.Timezone))
```

### 2.3 DTOs

**Location:** `SharpClaw.API/Agents/ScheduledJobs/ScheduledJobDto.cs`

```csharp
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
    string Timezone,   // optional, default "Europe/Rome"
    string Prompt,
    long AgentId,
    bool Enabled        // optional, default true
);

public record UpdateScheduledJobRequest(
    string? Name,
    string? CronExpression,
    string? Timezone,
    string? Prompt,
    long? AgentId,
    bool? Enabled
);
```

---

## 3. Backend: Cron Scheduler (BackgroundService)

### 3.1 `CronScheduler.cs`

**Location:** `SharpClaw.API/Agents/ScheduledJobs/CronScheduler.cs`

```csharp
public class CronScheduler : BackgroundService
{
    // Dependencies: ScheduledJobRepository, Agent, SessionStore, ILogger

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            await ProcessDueJobs(ct);
        }
    }

    private async Task ProcessDueJobs(CancellationToken ct)
    {
        var dueJobs = await repo.GetDueJobs();

        foreach (var job in dueJobs)
        {
            // Skip if previous run is still active
            if (job.LastSessionId is { } sid)
            {
                var session = await sessionStore.GetOrLoadSession(sid);
                var status = session.Run?.Status;
                if (status is AgentRunStatus.Pending
                          or AgentRunStatus.Waiting
                          or AgentRunStatus.Running)
                    continue; // skip overlapping run
            }

            // Fire: create session + enqueue prompt
            var sessionId = await agent.CreateSession(job.AgentId);
            await agent.EnqueueMessage(sessionId, job.Prompt);

            // Update job state
            var nextRunAt = ComputeNextRunAt(job);
            await repo.MarkFired(job.Id, sessionId, nextRunAt);
        }
    }
}
```

**Edge cases handled:**
- **Overlap:** Checks `last_session_id` run status; skips if still active.
- **Server restart:** `next_run_at` is already in the past → fires on next poll tick.
- **Disabled job:** Filtered by `GetDueJobs()` query.
- **Concurrent fires:** Each job is processed sequentially in the loop. Two instances of the scheduler won't run due to singleton registration.

### 3.2 Registration

In `Program.cs`:
```csharp
builder.Services.AddSingleton<ScheduledJobRepository>();
builder.Services.AddHostedService<CronScheduler>();
```

---

## 4. Backend: REST API Endpoints

### 4.1 `ScheduledJobEndpoints.cs`

**Location:** `SharpClaw.API/Endpoints/ScheduledJobEndpoints.cs`

| Method | Path | Handler | Notes |
|---|---|---|---|
| GET | `/jobs` | `GetAll()` | Returns all jobs, ordered by name |
| POST | `/jobs` | `Create(body)` | Validates cron, computes `next_run_at`, inserts |
| PATCH | `/jobs/{id}` | `Update(id, body)` | Partial update; recomputes `next_run_at` if cron/timezone changed |
| DELETE | `/jobs/{id}` | `Delete(id)` | Hard delete |

**Validation:**
- `cron_expression` must be parseable by Cronos. Return 400 with message if invalid.
- `timezone` must be a valid IANA timezone. Return 400 if not found.
- `agent_id` must reference an existing agent. Return 400 if not found.
- `name` and `prompt` must be non-empty.

**Response format:** Standard JSON, consistent with existing endpoints.

---

## 5. Backend: Agent Tools

### 5.1 `ScheduledJobTools.cs`

**Location:** `SharpClaw.API/Agents/ScheduledJobs/ScheduledJobTools.cs`

Four tools exposed to the agent:

| Tool | Parameters | Returns |
|---|---|---|
| `list_scheduled_jobs` | (none) | Array of `ScheduledJobDto` |
| `create_scheduled_job` | `name`, `cron_expression`, `prompt`, `agent_id?`, `timezone?` | Created `ScheduledJobDto` |
| `update_scheduled_job` | `id`, `name?`, `cron_expression?`, `prompt?`, `enabled?`, `timezone?` | Updated `ScheduledJobDto` |
| `delete_scheduled_job` | `id` | `{ deleted: true }` |

**Registration:** Add to `ToolCatalog.BuildTools()` in `Agent.cs`.

**Implementation pattern:** Each tool method takes `AIFunctionArguments`, extracts parameters, delegates to `ScheduledJobRepository`, returns JSON-serializable result.

---

## 6. Frontend: UI

### 6.1 New page: `ScheduledJobsPage.tsx`

**Location:** `sharpclaw-web/src/app/pages/ScheduledJobsPage.tsx`

**Layout:**
- Table listing all jobs: name, cron, timezone, agent, enabled toggle, last run, next run, actions
- "Create Job" button → opens modal/form
- Each row: edit (pencil icon), delete (trash icon), enable/disable toggle
- Form fields: name, cron expression (with human-readable preview), timezone dropdown, prompt (textarea), agent dropdown

### 6.2 Types

Add to `sharpclaw-web/src/app/types/chat.ts`:
```typescript
export type ScheduledJob = {
  id: number
  name: string
  cronExpression: string
  timezone: string
  prompt: string
  agentId: number
  enabled: boolean
  lastRunAt: string | null
  lastSessionId: string | null
  nextRunAt: string
  createdAt: string
  updatedAt: string
}
```

### 6.3 Navigation

Add "Scheduled Jobs" link to the app shell/navigation.

---

## 7. Implementation Order

| Step | What | Dependencies |
|---|---|---|
| 1 | Migration SQL + run it | None |
| 2 | `ScheduledJob.cs` model | None |
| 3 | `ScheduledJobRepository.cs` | Model, DB |
| 4 | `ScheduledJobDto.cs` + request DTOs | Model |
| 5 | `ScheduledJobEndpoints.cs` | Repository, DTOs |
| 6 | `CronScheduler.cs` | Repository, Agent, SessionStore |
| 7 | Register in `Program.cs` | All above |
| 8 | `ScheduledJobTools.cs` | Repository |
| 9 | Register tools in `ToolCatalog` | Tools |
| 10 | Frontend types | None |
| 11 | `ScheduledJobsPage.tsx` | Types, API |
| 12 | Navigation wiring | Page |
| 13 | Integration tests | All above |

---

## 8. Testing Plan

### Unit/Integration Tests

| Test | What it verifies |
|---|---|
| `CreateJob_WithValidCron_ComputesNextRunAt` | Cronos parses correctly, next_run_at is in the future |
| `CreateJob_WithInvalidCron_Returns400` | Validation rejects bad cron expressions |
| `CreateJob_WithInvalidTimezone_Returns400` | Validation rejects bad timezone |
| `GetDueJobs_ReturnsOnlyEnabledJobsWithPastNextRunAt` | Repository query filters correctly |
| `GetDueJobs_SkipsJobWithActiveLastSession` | Overlap prevention works |
| `MarkFired_UpdatesLastRunAndNextRun` | State update is correct |
| `UpdateJob_ChangingCron_RecomputesNextRunAt` | next_run_at is recalculated |
| `DeleteJob_RemovesFromDb` | Hard delete works |
| `ListJobs_ReturnsAllOrderedByName` | GET endpoint works |

---

## 9. Open Decisions

1. **Cron format:** 5-field (minute hour day month weekday) — standard Unix cron. Cronos supports this natively. No seconds field.

2. **Timezone list for UI:** Use `Intl.supportedValuesOf('timeZone')` in the frontend for the dropdown. Backend validates against `TimeZoneInfo.FindSystemTimeZoneById()`.

3. **Job run history:** Deferred to v2. For now, `last_run_at` + `last_session_id` give basic visibility. A full `scheduled_job_runs` table can be added later.

4. **Notification on failure:** If the agent session fails, the job still updates `next_run_at` and will fire again on schedule. No alerting for v1.
