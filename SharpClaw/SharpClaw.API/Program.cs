using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Agents;
using SharpClaw.API.Database;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddTransient<DatabaseSeeder>();
builder.Services.Configure<LmStudioConfiguration>(builder.Configuration.GetSection("LmStudio"));
builder.Services.AddSingleton<ChatProvider>();
builder.Services.AddSingleton<Agent>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebClient", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173", "https://localhost:5173")
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

var seeder = app.Services.GetRequiredService<DatabaseSeeder>();
await seeder.Seed();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("WebClient");

app.MapPost("/chat", async (
    [FromBody] MessageRequest request,
    [FromServices] Agent agent
) =>
{
    var sessionId = await agent.CreateSession(1);
    var run = await agent.EnqueueMessage(sessionId, request.Message);

    while (run.Status is AgentRunStatus.Pending or AgentRunStatus.Running)
        await Task.Delay(100);

    if (run.Status == AgentRunStatus.Failed)
        return Results.Problem(run.Error ?? "Agent run failed.");

    var text = string.Concat(run.GetEventsAfter(0)
        .Where(e => e.Type == "delta")
        .Select(e => e.Text));

    return Results.Ok(new
    {
        sessionId,
        runId = run.RunId,
        response = text,
    });
});

app.MapPost("/sessions", async (
    [FromBody] CreateSessionRequest? request,
    [FromServices] Agent agent
) =>
{
    var sessionId = await agent.CreateSession(request?.AgentId ?? 1);
    return Results.Ok(new { sessionId });
});

app.MapPost("/sessions/{sessionId:guid}/messages", async (
    Guid sessionId,
    [FromBody] MessageRequest request,
    [FromServices] Agent agent
) =>
{
    try
    {
        var run = await agent.EnqueueMessage(sessionId, request.Message);
        return Results.Ok(new
        {
            runId = run.RunId,
            sessionId = run.SessionId,
            status = run.Status.ToString().ToLowerInvariant(),
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapGet("/agents/{agentId:long}/sessions", (
    long agentId,
    [FromServices] Agent agent
) =>
{
    var sessions = agent.GetSessions(agentId);
    return Results.Ok(new
    {
        agentId,
        sessions,
    });
});

app.MapGet("/sessions/{sessionId:guid}/history", (
    Guid sessionId,
    [FromServices] Agent agent
) =>
{
    try
    {
        var history = agent.GetHistory(sessionId);
        return Results.Ok(new
        {
            sessionId,
            messages = history,
        });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapGet("/sessions/{sessionId:guid}/runs/{runId:guid}", (
    Guid sessionId,
    Guid runId,
    [FromServices] Agent agent
) =>
{
    try
    {
        var run = agent.GetRun(sessionId, runId);
        return Results.Ok(new
        {
            runId = run.RunId,
            sessionId = run.SessionId,
            createdAt = run.CreatedAt,
            startedAt = run.StartedAt,
            completedAt = run.CompletedAt,
            status = run.Status.ToString().ToLowerInvariant(),
            error = run.Error,
        });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapGet("/sessions/{sessionId:guid}/runs/{runId:guid}/stream", async (
    Guid sessionId,
    Guid runId,
    HttpContext httpContext,
    [FromServices] Agent agent
) =>
{
    AgentRunState run;
    try
    {
        run = agent.GetRun(sessionId, runId);
    }
    catch (KeyNotFoundException ex)
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, httpContext.RequestAborted);
        return;
    }

    httpContext.Response.Headers.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    long cursor = 0;
    while (!httpContext.RequestAborted.IsCancellationRequested)
    {
        var events = run.GetEventsAfter(cursor);
        foreach (var ev in events)
        {
            cursor = ev.Sequence;
            var payload = JsonSerializer.Serialize(new
            {
                runId = run.RunId,
                sessionId = run.SessionId,
                sequence = ev.Sequence,
                type = ev.Type,
                text = ev.Text,
                timestamp = ev.Timestamp,
                status = run.Status.ToString().ToLowerInvariant(),
            });

            await httpContext.Response.WriteAsync($"event: {ev.Type}\n", httpContext.RequestAborted);
            await httpContext.Response.WriteAsync($"data: {payload}\n\n", httpContext.RequestAborted);
        }

        await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);

        if (run.Status is AgentRunStatus.Completed or AgentRunStatus.Failed)
            break;

        await Task.Delay(100, httpContext.RequestAborted);
    }
});

app.Run();

public class CreateSessionRequest
{
    public long? AgentId { get; set; }
}

public class MessageRequest
{
    public required string Message { get; set; }
}
