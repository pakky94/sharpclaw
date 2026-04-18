using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Agents;

namespace SharpClaw.API.Endpoints;

public static class ChatEndpoints
{
    public static void Register(WebApplication app)
    {
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
                    latestSequenceId = run.StartMessageId,
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

        app.MapPost("/sessions/{sessionId:guid}/resume", async (
            Guid sessionId,
            [FromServices] Agent agent
        ) =>
        {
            try
            {
                await agent.Resume(sessionId);
                return Results.Accepted();
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

        app.MapGet("/agents/{agentId:long}/sessions", async (
            long agentId,
            [FromServices] Agent agent
        ) =>
        {
            var sessions = await agent.GetSessions(agentId);
            return Results.Ok(new
            {
                agentId,
                sessions,
            });
        });

        app.MapGet("/sessions/{sessionId:guid}/history", async (
            Guid sessionId,
            [FromServices] Agent agent
        ) =>
        {
            try
            {
                var history = await agent.GetHistory(sessionId);
                return Results.Ok(new
                {
                    sessionId = history.SessionId,
                    parentSessionId = history.ParentSessionId,
                    latestSequenceId = history.LatestSequenceId,
                    runStatus = history.RunStatus,
                    messages = history.Messages,
                    childSessions = history.ChildSessions,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapGet("/sessions/{sessionId:guid}/messages/{latestSequenceId:long}/stream", async (
            Guid sessionId,
            long latestSequenceId,
            HttpContext httpContext,
            [FromServices] Agent agent
        ) =>
        {
            AgentSessionState session;
            try
            {
                session = await agent.GetOrLoadSession(sessionId);
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

            long cursor = 0; // TODO: get cursor from latestSequenceId
            while (!httpContext.RequestAborted.IsCancellationRequested)
            {
                var events = session.Run?.GetEventsAfter(latestSequenceId, cursor) ?? [];
                foreach (var ev in events)
                {
                    cursor = ev.Sequence;
                    var payload = JsonSerializer.Serialize(new
                    {
                        sessionId = session.SessionId,
                        sequence = ev.Sequence,
                        messageId = ev.MessageId,
                        type = ev.Type,
                        text = ev.Text,
                        data = ev.Data,
                        timestamp = ev.Timestamp,
                        status = session.Run?.Status.ToString().ToLowerInvariant(),
                    });

                    await httpContext.Response.WriteAsync($"event: {ev.Type}\n", httpContext.RequestAborted);
                    await httpContext.Response.WriteAsync($"data: {payload}\n\n", httpContext.RequestAborted);
                }

                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);

                if (session.Run?.Status is AgentRunStatus.Completed or AgentRunStatus.Failed)
                    break;

                await Task.Delay(100, httpContext.RequestAborted);
            }
        });
    }
}

public class CreateSessionRequest
{
    public long? AgentId { get; set; }
}

public class MessageRequest
{
    public required string Message { get; set; }
    public long? AgentId { get; set; }
}
