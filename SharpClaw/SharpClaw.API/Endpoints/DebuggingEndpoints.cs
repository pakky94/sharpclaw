using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.API.Agents;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database;
using SharpClaw.API.Database.Repositories;
using SharpClaw.API.Web;

namespace SharpClaw.API.Endpoints;

public static class DebuggingEndpoints
{
    public static void Register(WebApplication app)
    {
        app.MapPost("/debugging/tool-call", async (
            [FromBody] DebugToolCallRequest request,
            [FromServices] IServiceProvider serviceProvider,
            [FromServices] AgentsRepository agentsRepository,
            [FromServices] WorkspaceRepository workspaceRepository
        ) =>
        {
            var toolName = request.ToolName.Trim();
            if (string.IsNullOrWhiteSpace(toolName))
                return Results.BadRequest(new { error = "toolName is required." });

            var agentId = request.AgentId ?? 1;
            var agent = await agentsRepository.GetAgent(agentId);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent {agentId} was not found." });

            var tools = ToolCatalog.BuildTools();
            var tool = tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
            if (tool is null)
                return Results.NotFound(new { error = $"Tool '{toolName}' is not registered." });

            var resolvedWorkspace = await ResolveWorkspace(request, workspaceRepository, agentId);
            if (resolvedWorkspace is null && !string.IsNullOrWhiteSpace(request.WorkspaceName))
                return Results.BadRequest(new { error = $"Workspace '{request.WorkspaceName}' not found for agent {agentId}." });

            var context = new AgentExecutionContext
            {
                SessionId = Guid.NewGuid(),
                AgentId = agentId,
                LlmModel = agent.LlmModel,
                Temperature = agent.Temperature,
                Messages = [],
                Workspace = resolvedWorkspace,
                ActiveWorkspaceNames = resolvedWorkspace is null
                    ? []
                    : [resolvedWorkspace.Name],
            };

            if (request.UnrestrictedWorkspace)
            {
                context.Workspace = context.Workspace is null
                    ? null
                    : new ResolvedWorkspace
                    {
                        Workspace = context.Workspace.Workspace,
                        PolicyMode = WorkspacePolicyMode.TrueUnrestricted,
                        IsDefault = context.Workspace.IsDefault,
                    };
            }

            var arguments = request.Arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(request.Arguments.GetRawText())
                  ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            var functionArguments = new AIFunctionArguments(arguments)
            {
                Services = BuildInvocationServices(serviceProvider, context),
                Context = new Dictionary<object, object?>
                {
                    ["CallId"] = request.CallId ?? $"debug_{Guid.NewGuid():N}",
                },
            };

            object? result;
            try
            {
                result = await tool.InvokeAsync(functionArguments);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    toolName,
                    callId = functionArguments.Context?["CallId"],
                    error = ex.Message,
                });
            }

            return Results.Ok(new
            {
                toolName,
                callId = functionArguments.Context?["CallId"],
                result,
            });
        });
    }

    private static async Task<ResolvedWorkspace?> ResolveWorkspace(DebugToolCallRequest request, WorkspaceRepository workspaceRepository, long agentId)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkspaceName))
            return await workspaceRepository.ResolveWorkspaceByName(agentId, request.WorkspaceName);

        return await workspaceRepository.ResolveDefaultWorkspace(agentId);
    }

    private static IServiceProvider BuildInvocationServices(IServiceProvider rootServices, AgentExecutionContext context)
    {
        var services = new ServiceCollection()
            .AddSingleton(context)
            .AddSingleton(rootServices.GetRequiredService<IConfiguration>())
            .AddSingleton(rootServices.GetRequiredService<ChatRepository>())
            .AddSingleton(rootServices.GetRequiredService<AgentsRepository>())
            .AddSingleton(rootServices.GetRequiredService<FragmentsRepository>())
            .AddSingleton(rootServices.GetRequiredService<FragmentEmbeddingService>())
            .AddSingleton(rootServices.GetRequiredService<WorkspaceRepository>())
            .AddSingleton(rootServices.GetRequiredService<ApprovalService>())
            .AddSingleton(rootServices.GetRequiredService<ISearchService>())
            .AddSingleton(rootServices.GetRequiredService<IWebFetchService>());

        return services.BuildServiceProvider();
    }
}

public class DebugToolCallRequest
{
    public required string ToolName { get; set; }
    public JsonElement Arguments { get; set; }
    public long? AgentId { get; set; }
    public string? CallId { get; set; }
    public string? WorkspaceName { get; set; }
    public bool UnrestrictedWorkspace { get; set; } = true;
}
