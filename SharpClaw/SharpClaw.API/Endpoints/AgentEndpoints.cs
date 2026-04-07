using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Database;

namespace SharpClaw.API.Endpoints;

public static class AgentEndpoints
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/agents", async ([FromServices] Repository repository) =>
        {
            var agents = await repository.GetAgents();
            return Results.Ok(new { agents });
        });

        app.MapGet("/agents/{agentId:long}", async (
            long agentId,
            [FromServices] Repository repository
        ) =>
        {
            var agent = await repository.GetAgent(agentId);
            return agent is null
                ? Results.NotFound(new { error = $"Agent {agentId} was not found." })
                : Results.Ok(agent);
        });

        app.MapPost("/agents", async (
            [FromBody] CreateAgentRequest request,
            [FromServices] Repository repository,
            [FromServices] FragmentsRepository fragmentsRepository
        ) =>
        {
            var name = request.Name.Trim();
            var model = string.IsNullOrWhiteSpace(request.LlmModel) ? "openai/gpt-oss-20b" : request.LlmModel.Trim();
            var temperature = request.Temperature ?? 0.1f;

            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "Name is required." });

            var created = await repository.CreateAgent(name, model, temperature);
            await fragmentsRepository.EnsureRootFragment(created.Id);
            await fragmentsRepository.UpsertFragmentByPath(created.Id, "AGENTS.md", DatabaseSeeder.AgentsMd);
            return Results.Ok(created);
        });

        app.MapPut("/agents/{agentId:long}", async (
            long agentId,
            [FromBody] UpdateAgentRequest request,
            [FromServices] Repository repository
        ) =>
        {
            var name = request.Name.Trim();
            var model = string.IsNullOrWhiteSpace(request.LlmModel) ? "openai/gpt-oss-20b" : request.LlmModel.Trim();
            var temperature = request.Temperature ?? 0.1f;

            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "Name is required." });

            var updated = await repository.UpdateAgent(agentId, name, model, temperature);
            return updated is null
                ? Results.NotFound(new { error = $"Agent {agentId} was not found." })
                : Results.Ok(updated);
        });

        app.MapGet("/agents/{agentId:long}/fragments", async (
            long agentId,
            [FromQuery] string? parentPath,
            [FromServices] Repository repository,
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
            [FromServices] Repository repository,
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
}

public class UpdateAgentRequest
{
    public required string Name { get; set; }
    public string? LlmModel { get; set; }
    public float? Temperature { get; set; }
}

public class UpsertAgentFileRequest
{
    public required string Path { get; set; }
    public required string Content { get; set; }
}
