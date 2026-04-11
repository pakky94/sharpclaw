using Microsoft.Extensions.AI;
using SharpClaw.API.Database;

namespace SharpClaw.API.Agents.Tools.Workspace;

public static class SessionWorkspaceTools
{
    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(ListActiveWorkspaces, "ws_list_active_workspaces", "List the workspaces currently active for this session"),
        AIFunctionFactory.Create(ListAvailableWorkspaces, "ws_list_available_workspaces", "List all workspaces available to the agent, including which ones are currently active"),
        AIFunctionFactory.Create(SetActiveWorkspaces, "ws_set_active_workspaces", "Set which workspaces are active for this session. Only workspaces assigned to the agent can be activated."),
    ];

    private static async Task<object> ListActiveWorkspaces(IServiceProvider sp)
    {
        var ctx = sp.GetRequiredService<AgentExecutionContext>();
        var repo = sp.GetRequiredService<WorkspaceRepository>();

        var activeRows = await repo.GetActiveWorkspacesForSession(ctx.SessionId);

        if (activeRows.Count == 0 && ctx.Workspace is not null)
        {
            return new
            {
                active_workspaces = new[]
                {
                    new { name = ctx.Workspace.Name, policy_mode = ctx.Workspace.PolicyMode.ToString().ToLowerInvariant(), is_default = ctx.Workspace.IsDefault },
                },
                count = 1,
            };
        }

        var items = activeRows.Select(r => new
        {
            name = r.WorkspaceName,
            policy_mode = r.PolicyMode.ToString().ToLowerInvariant(),
        }).ToArray();

        return new
        {
            active_workspaces = items,
            count = items.Length,
        };
    }

    private static async Task<object> ListAvailableWorkspaces(IServiceProvider sp)
    {
        var ctx = sp.GetRequiredService<AgentExecutionContext>();
        var repo = sp.GetRequiredService<WorkspaceRepository>();

        var available = await repo.GetAvailableWorkspacesForAgent(ctx.AgentId);

        var items = available.Select(w => new
        {
            name = w.Name,
            root_path = w.RootPath,
            policy_mode = w.PolicyMode.ToString().ToLowerInvariant(),
            is_default = w.IsDefault,
            is_active = ctx.ActiveWorkspaceNames.Contains(w.Name, StringComparer.OrdinalIgnoreCase),
        }).ToArray();

        return new
        {
            available_workspaces = items,
            count = items.Length,
        };
    }

    private static async Task<object> SetActiveWorkspaces(IServiceProvider sp, string[] workspace_names)
    {
        var ctx = sp.GetRequiredService<AgentExecutionContext>();
        var repo = sp.GetRequiredService<WorkspaceRepository>();

        var available = await repo.GetAvailableWorkspacesForAgent(ctx.AgentId);
        var availableNames = new HashSet<string>(available.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);

        var invalid = workspace_names.Where(n => !availableNames.Contains(n)).ToArray();
        if (invalid.Length > 0)
        {
            return new
            {
                error = $"The following workspaces are not assigned to this agent: {string.Join(", ", invalid)}",
                available_workspaces = available.Select(w => w.Name).ToArray(),
            };
        }

        await repo.SetActiveWorkspacesForSession(ctx.SessionId, ctx.AgentId, workspace_names);

        ctx.ActiveWorkspaceNames = new HashSet<string>(workspace_names, StringComparer.OrdinalIgnoreCase);

        var defaultWs = available.FirstOrDefault(w => w.IsDefault);
        if (defaultWs is not null && workspace_names.Contains(defaultWs.Name, StringComparer.OrdinalIgnoreCase))
            ctx.Workspace = defaultWs;
        else if (workspace_names.Length > 0)
        {
            var first = available.FirstOrDefault(w => workspace_names.Contains(w.Name, StringComparer.OrdinalIgnoreCase));
            if (first is not null)
                ctx.Workspace = first;
        }

        return new
        {
            message = $"Active workspaces updated: {string.Join(", ", workspace_names)}",
            active_workspaces = workspace_names,
            count = workspace_names.Length,
        };
    }
}
