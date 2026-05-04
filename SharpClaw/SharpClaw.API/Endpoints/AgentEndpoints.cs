using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Database;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Endpoints;

public static class AgentEndpoints
{
    private const long DefaultSoftCompactThreshold = 75 * 1024;
    private const long DefaultHardCompactThreshold = 85 * 1024;

    public static void Register(WebApplication app)
    {
        app.MapGet("/agents", async ([FromServices] AgentsRepository repository) =>
        {
            var agents = await repository.GetAgents();
            return Results.Ok(new { agents });
        });

        app.MapGet("/agents/{agentId:long}", async (
            long agentId,
            [FromServices] AgentsRepository repository
        ) =>
        {
            var agent = await repository.GetAgent(agentId);
            return agent is null
                ? Results.NotFound(new { error = $"Agent {agentId} was not found." })
                : Results.Ok(agent);
        });

        app.MapPost("/agents", async (
            [FromBody] CreateAgentRequest request,
            [FromServices] AgentsRepository repository,
            [FromServices] FragmentsRepository fragmentsRepository
        ) =>
        {
            var name = request.Name.Trim();
            var model = string.IsNullOrWhiteSpace(request.LlmModel) ? "openai/gpt-oss-20b" : request.LlmModel.Trim();
            var temperature = request.Temperature ?? 0.1f;
            var softCompactThreshold = request.SoftCompactThreshold ?? DefaultSoftCompactThreshold;
            var hardCompactThreshold = request.HardCompactThreshold ?? DefaultHardCompactThreshold;

            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "Name is required." });
            if (softCompactThreshold <= 0)
                return Results.BadRequest(new { error = "Soft compact threshold must be greater than 0." });
            if (hardCompactThreshold <= 0)
                return Results.BadRequest(new { error = "Hard compact threshold must be greater than 0." });
            if (hardCompactThreshold <= softCompactThreshold)
                return Results.BadRequest(new { error = "Hard compact threshold must be greater than soft compact threshold." });

            var created = await repository.CreateAgent(
                name,
                model,
                temperature,
                softCompactThreshold,
                hardCompactThreshold);
            await fragmentsRepository.EnsureRootFragment(created.Id);
            await fragmentsRepository.UpsertFragmentByPath(created.Id, "AGENTS.md", DatabaseSeeder.AgentsMd);
            return Results.Ok(created);
        });

        app.MapPut("/agents/{agentId:long}", async (
            long agentId,
            [FromBody] UpdateAgentRequest request,
            [FromServices] AgentsRepository repository
        ) =>
        {
            var existing = await repository.GetAgent(agentId);
            if (existing is null)
                return Results.NotFound(new { error = $"Agent {agentId} was not found." });

            var name = request.Name.Trim();
            var model = string.IsNullOrWhiteSpace(request.LlmModel) ? "openai/gpt-oss-20b" : request.LlmModel.Trim();
            var temperature = request.Temperature ?? 0.1f;
            var softCompactThreshold = request.SoftCompactThreshold ?? existing.SoftCompactThreshold;
            var hardCompactThreshold = request.HardCompactThreshold ?? existing.HardCompactThreshold;

            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "Name is required." });
            if (softCompactThreshold <= 0)
                return Results.BadRequest(new { error = "Soft compact threshold must be greater than 0." });
            if (hardCompactThreshold <= 0)
                return Results.BadRequest(new { error = "Hard compact threshold must be greater than 0." });
            if (hardCompactThreshold <= softCompactThreshold)
                return Results.BadRequest(new { error = "Hard compact threshold must be greater than soft compact threshold." });

            var updated = await repository.UpdateAgent(
                agentId,
                name,
                model,
                temperature,
                softCompactThreshold,
                hardCompactThreshold);
            return updated is null
                ? Results.NotFound(new { error = $"Agent {agentId} was not found." })
                : Results.Ok(updated);
        });

        app.MapGet("/agents/{agentId:long}/fragments", async (
            long agentId,
            [FromQuery] string? parentPath,
            [FromServices] AgentsRepository repository,
            [FromServices] FragmentsRepository fragmentsRepository
        ) =>
        {
            var agent = await repository.GetAgent(agentId);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent {agentId} was not found." });

            var fragments = await fragmentsRepository.ListFragmentChildren(agentId, parentPath);
            return Results.Ok(new { agentId, parentPath, fragments });
        });

        app.MapGet("/agents/{agentId:long}/fragments/file", async (
            long agentId,
            [FromQuery] string path,
            [FromServices] FragmentsRepository fragmentsRepository
        ) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Path is required." });

            var file = await fragmentsRepository.ReadFragmentByPath(agentId, path);
            return file is null
                ? Results.NotFound(new { error = $"Fragment '{path}' was not found for agent {agentId}." })
                : Results.Ok(file);
        });

        app.MapPut("/agents/{agentId:long}/fragments/file", async (
            long agentId,
            [FromBody] UpsertAgentFileRequest request,
            [FromServices] AgentsRepository repository,
            [FromServices] FragmentsRepository fragmentsRepository
        ) =>
        {
            var path = request.Path.Trim();
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Path is required." });

            var agent = await repository.GetAgent(agentId);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent {agentId} was not found." });

            await fragmentsRepository.UpsertFragmentByPath(agentId, path, request.Content);
            return Results.Ok(new { agentId, path });
        });

        app.MapDelete("/agents/{agentId:long}/fragments/file", async (
            long agentId,
            [FromQuery] string path,
            [FromServices] FragmentsRepository fragmentsRepository
        ) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Path is required." });

            var deleted = await fragmentsRepository.DeleteFragmentByPath(agentId, path);
            return deleted
                ? Results.Ok(new { agentId, path })
                : Results.NotFound(new { error = $"Fragment '{path}' was not found for agent {agentId}." });
        });
    }
}

public class CreateAgentRequest
{
    public required string Name { get; set; }
    public string? LlmModel { get; set; }
    public float? Temperature { get; set; }
    public long? SoftCompactThreshold { get; set; }
    public long? HardCompactThreshold { get; set; }
}

public class UpdateAgentRequest
{
    public required string Name { get; set; }
    public string? LlmModel { get; set; }
    public float? Temperature { get; set; }
    public long? SoftCompactThreshold { get; set; }
    public long? HardCompactThreshold { get; set; }
}

public class UpsertAgentFileRequest
{
    public required string Path { get; set; }
    public required string Content { get; set; }
}
