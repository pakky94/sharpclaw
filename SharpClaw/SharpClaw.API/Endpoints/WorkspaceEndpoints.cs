using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Agents;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Endpoints;

public static class WorkspaceEndpoints
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/workspaces", async (
            [FromServices] WorkspaceRepository repository) =>
        {
            var workspaces = await repository.GetAllWorkspaces();
            return Results.Ok(new { workspaces });
        });

        app.MapGet("/workspaces/{workspaceId:long}", async (
            long workspaceId,
            [FromServices] WorkspaceRepository repository) =>
        {
            var workspace = await repository.GetWorkspaceById(workspaceId);
            return workspace is null
                ? Results.NotFound(new { error = $"Workspace {workspaceId} not found." })
                : Results.Ok(workspace);
        });

        app.MapPut("/workspaces", async (
            [FromBody] CreateWorkspaceRequest request,
            [FromServices] WorkspaceRepository repository) =>
        {
            var name = string.IsNullOrWhiteSpace(request.Name)
                ? throw new ArgumentException("Workspace name is required.")
                : request.Name;

            var rootPath = string.IsNullOrWhiteSpace(request.RootPath)
                ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SharpClaw", "workspaces", name)
                : request.RootPath;

            var allowlist = request.AllowlistPatterns ?? [];
            var denylist = request.DenylistPatterns ?? [];

            var workspace = await repository.UpsertWorkspace(name, rootPath, allowlist, denylist);
            return Results.Ok(workspace);
        });

        app.MapDelete("/workspaces/{workspaceId:long}", async (
            long workspaceId,
            [FromServices] WorkspaceRepository repository) =>
        {
            var deleted = await repository.DeleteWorkspace(workspaceId);
            return deleted
                ? Results.Ok(new { message = $"Workspace {workspaceId} deleted." })
                : Results.NotFound(new { error = $"Workspace {workspaceId} not found." });
        });

        app.MapGet("/agents/{agentId:long}/workspaces", async (
            long agentId,
            [FromServices] WorkspaceRepository repository,
            [FromServices] AgentsRepository repo) =>
        {
            var agent = await repo.GetAgent(agentId);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent {agentId} not found." });

            var assignments = await repository.GetAssignmentsForAgent(agentId);
            var workspaces = new List<object>();

            foreach (var assignment in assignments)
            {
                var ws = await repository.GetWorkspaceById(assignment.WorkspaceId);
                if (ws is not null)
                {
                    workspaces.Add(new
                    {
                        workspace = ws,
                        assignment = new
                        {
                            assignment.Id,
                            assignment.PolicyMode,
                            assignment.IsDefault,
                        },
                    });
                }
            }

            return Results.Ok(new { agentId, workspaces });
        });

        app.MapPut("/agents/{agentId:long}/workspaces/{workspaceId:long}", async (
            long agentId,
            long workspaceId,
            [FromBody] AssignWorkspaceRequest request,
            [FromServices] WorkspaceRepository repository,
            [FromServices] AgentsRepository repo) =>
        {
            var agent = await repo.GetAgent(agentId);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent {agentId} not found." });

            var ws = await repository.GetWorkspaceById(workspaceId);
            if (ws is null)
                return Results.NotFound(new { error = $"Workspace {workspaceId} not found." });

            var policyMode = request.PolicyMode ?? WorkspacePolicyMode.ConfirmWritesAndExec;

            var assignment = await repository.UpsertAssignment(agentId, workspaceId, policyMode, request.IsDefault ?? false);
            return Results.Ok(assignment);
        });

        app.MapDelete("/agents/{agentId:long}/workspaces/{workspaceId:long}", async (
            long agentId,
            long workspaceId,
            [FromServices] WorkspaceRepository repository) =>
        {
            var deleted = await repository.DeleteAssignment(agentId, workspaceId);
            return deleted
                ? Results.Ok(new { message = $"Assignment removed." })
                : Results.NotFound(new { error = $"Assignment not found." });
        });

        app.MapPost("/sessions/{sessionId:guid}/approvals/{token}/approve", async (
            Guid sessionId,
            string token,
            [FromServices] Agent agent,
            [FromServices] ApprovalService approvalService) =>
        {
            var run = agent.GetActiveRunForSession(sessionId);
            if (run is not null)
            {
                var resolved = run.ResolveApproval(token, true);
                if (resolved)
                    return Results.Ok(new { message = "Approval granted." });
            }

            var approval = await approvalService.ValidateApprovalToken(token);
            if (approval is null)
                return Results.NotFound(new { error = "Invalid or already resolved approval token." });

            if (approval.SessionId != sessionId)
                return Results.BadRequest(new { error = "Approval token does not belong to this session." });

            var resolvedDb = await approvalService.ResolveApproval(token, true);
            return resolvedDb
                ? Results.Ok(new { message = "Approval granted." })
                : Results.BadRequest(new { error = "Failed to resolve approval." });
        });

        app.MapPost("/sessions/{sessionId:guid}/approvals/{token}/reject", async (
            Guid sessionId,
            string token,
            [FromServices] Agent agent,
            [FromServices] ApprovalService approvalService) =>
        {
            var run = agent.GetActiveRunForSession(sessionId);
            if (run is not null)
            {
                var resolved = run.ResolveApproval(token, false);
                if (resolved)
                    return Results.Ok(new { message = "Approval rejected." });
            }

            var approval = await approvalService.ValidateApprovalToken(token);
            if (approval is null)
                return Results.NotFound(new { error = "Invalid or already resolved approval token." });

            if (approval.SessionId != sessionId)
                return Results.BadRequest(new { error = "Approval token does not belong to this session." });

            var resolvedDb = await approvalService.ResolveApproval(token, false);
            return resolvedDb
                ? Results.Ok(new { message = "Approval rejected." })
                : Results.BadRequest(new { error = "Failed to resolve approval." });
        });

        app.MapGet("/sessions/{sessionId:guid}/approvals/pending", async (
            Guid sessionId,
            [FromServices] ApprovalService approvalService) =>
        {
            var pending = await approvalService.GetPendingApprovalsForSession(sessionId);
            return Results.Ok(new { sessionId, approvals = pending });
        });

        app.MapGet("/sessions/{sessionId:guid}/active-workspaces", async (
            Guid sessionId,
            [FromServices] WorkspaceRepository repository,
            [FromServices] ChatRepository repo) =>
        {
            var session = await repo.GetSession(sessionId);
            if (session is null)
                return Results.NotFound(new { error = $"Session {sessionId} not found." });

            var active = await repository.GetActiveWorkspacesForSession(sessionId);
            var items = active.Select(r => new
            {
                name = r.WorkspaceName,
                policyMode = r.PolicyMode.ToString().ToLowerInvariant(),
            }).ToArray();

            return Results.Ok(new { sessionId, activeWorkspaces = items });
        });

        app.MapPut("/sessions/{sessionId:guid}/active-workspaces", async (
            Guid sessionId,
            [FromBody] SetActiveWorkspacesRequest request,
            [FromServices] WorkspaceRepository repository,
            [FromServices] ChatRepository repo,
            [FromServices] Agent agent) =>
        {
            var session = await repo.GetSession(sessionId);
            if (session is null)
                return Results.NotFound(new { error = $"Session {sessionId} not found." });

            var names = request.WorkspaceNames ?? [];
            await repository.SetActiveWorkspacesForSession(sessionId, session.AgentId, names);

            agent.TryUpdateSessionActiveWorkspaces(sessionId, names);

            var active = await repository.GetActiveWorkspacesForSession(sessionId);
            var items = active.Select(r => new
            {
                name = r.WorkspaceName,
                policyMode = r.PolicyMode.ToString().ToLowerInvariant(),
            }).ToArray();

            return Results.Ok(new { sessionId, activeWorkspaces = items });
        });

        app.MapGet("/agents/{agentId:long}/available-workspaces", async (
            long agentId,
            [FromServices] WorkspaceRepository repository,
            [FromServices] AgentsRepository repo) =>
        {
            var agent = await repo.GetAgent(agentId);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent {agentId} not found." });

            var available = await repository.GetAvailableWorkspacesForAgent(agentId);
            var items = available.Select(w => new
            {
                workspace = w.Workspace,
                policyMode = w.PolicyMode.ToString().ToLowerInvariant(),
                isDefault = w.IsDefault,
            }).ToArray();

            return Results.Ok(new { agentId, availableWorkspaces = items });
        });
    }
}

public class CreateWorkspaceRequest
{
    public required string Name { get; set; }
    public string? RootPath { get; set; }
    public string[]? AllowlistPatterns { get; set; }
    public string[]? DenylistPatterns { get; set; }
}

public class AssignWorkspaceRequest
{
    public WorkspacePolicyMode? PolicyMode { get; set; }
    public bool? IsDefault { get; set; }
}

public class SetActiveWorkspacesRequest
{
    public string[]? WorkspaceNames { get; set; }
}
