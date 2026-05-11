using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Agents.Channels;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Endpoints;

public static class ChannelEndpoints
{
    private static readonly HashSet<string> ValidTypes = ["discord", "telegram"];
    private static readonly HashSet<string> ValidRoutingModes = ["shared", "per_user"];

    public static void Register(WebApplication app)
    {
        app.MapGet("/channels", async (
            [FromServices] ChannelRepository repo) =>
        {
            var channels = await repo.GetAll();
            return Results.Ok(new
            {
                channels = channels.Select(MapToDto),
            });
        });

        app.MapPost("/channels", async (
            [FromBody] CreateChannelRequest request,
            [FromServices] ChannelRepository repo,
            [FromServices] AgentsRepository agentsRepo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required." });

            if (!ValidTypes.Contains(request.Type))
                return Results.BadRequest(new { error = $"Invalid channel type: '{request.Type}'. Must be one of: {string.Join(", ", ValidTypes)}" });

            var routingMode = request.RoutingMode ?? "shared";
            if (!ValidRoutingModes.Contains(routingMode))
                return Results.BadRequest(new { error = $"Invalid routing mode: '{routingMode}'. Must be one of: {string.Join(", ", ValidRoutingModes)}" });

            var agent = await agentsRepo.GetAgent(request.AgentId);
            if (agent is null)
                return Results.BadRequest(new { error = $"Agent {request.AgentId} not found." });

            var channel = new Channel
            {
                Name = request.Name,
                Type = request.Type,
                AgentId = request.AgentId,
                RoutingMode = routingMode,
                Config = request.Config ?? "{}",
                Enabled = request.Enabled ?? true,
            };

            var created = await repo.Create(channel);
            return Results.Ok(MapToDto(created));
        });

        app.MapPatch("/channels/{id:long}", async (
            long id,
            [FromBody] UpdateChannelRequest request,
            [FromServices] ChannelRepository repo,
            [FromServices] AgentsRepository agentsRepo) =>
        {
            var existing = await repo.GetById(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Channel {id} not found." });

            if (request.Type is not null && !ValidTypes.Contains(request.Type))
                return Results.BadRequest(new { error = $"Invalid channel type: '{request.Type}'. Must be one of: {string.Join(", ", ValidTypes)}" });

            if (request.RoutingMode is not null && !ValidRoutingModes.Contains(request.RoutingMode))
                return Results.BadRequest(new { error = $"Invalid routing mode: '{request.RoutingMode}'. Must be one of: {string.Join(", ", ValidRoutingModes)}" });

            if (request.AgentId is { } agentId)
            {
                var agent = await agentsRepo.GetAgent(agentId);
                if (agent is null)
                    return Results.BadRequest(new { error = $"Agent {agentId} not found." });
            }

            var updated = new Channel
            {
                Id = id,
                Name = request.Name ?? existing.Name,
                Type = request.Type ?? existing.Type,
                AgentId = request.AgentId ?? existing.AgentId,
                RoutingMode = request.RoutingMode ?? existing.RoutingMode,
                Config = request.Config ?? existing.Config,
                Enabled = request.Enabled ?? existing.Enabled,
            };

            var result = await repo.Update(id, updated);
            if (result is null)
                return Results.NotFound(new { error = $"Channel {id} not found." });

            return Results.Ok(MapToDto(result));
        });

        app.MapDelete("/channels/{id:long}", async (
            long id,
            [FromServices] ChannelRepository repo) =>
        {
            var deleted = await repo.Delete(id);
            if (!deleted)
                return Results.NotFound(new { error = $"Channel {id} not found." });

            return Results.Ok(new { deleted = true });
        });
    }

    private static ChannelDto MapToDto(Channel channel) => new(
        channel.Id,
        channel.Name,
        channel.Type,
        channel.AgentId,
        channel.RoutingMode,
        channel.Config,
        channel.Enabled,
        channel.CreatedAt,
        channel.UpdatedAt
    );
}
