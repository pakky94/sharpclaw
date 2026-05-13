using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Endpoints;

public static class BridgeEndpoints
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/bridge/connect", async (HttpContext context,
            [FromServices] BridgeConnectionManager connectionManager,
            [FromServices] ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("BridgeEndpoints");

            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest(new { error = "WebSocket connection required." });
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            var bridgeId = Guid.NewGuid().ToString("N");

            logger.LogInformation("Bridge connecting: {BridgeId}", bridgeId);

            await connectionManager.HandleBridgeConnection(bridgeId, socket);

            return Results.Empty;
        });

        app.MapGet("/bridge/status", (
            [FromServices] BridgeConnectionManager connectionManager) =>
        {
            var bridges = connectionManager.GetConnectedBridges();
            return Results.Ok(new { bridges });
        });

        app.MapPost("/bridge/{bridgeId}/disconnect", (
            string bridgeId,
            [FromServices] BridgeConnectionManager connectionManager) =>
        {
            connectionManager.DisconnectBridge(bridgeId);
            return Results.Ok(new { message = $"Bridge {bridgeId} disconnected." });
        });
    }
}
