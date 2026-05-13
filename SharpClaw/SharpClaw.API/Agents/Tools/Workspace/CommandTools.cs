using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Agents.Tools.Workspace;

public static partial class CommandTools
{
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxOutputBytes = 50_000;

    [GeneratedRegex(@"(?i)rm\s+(-rf?|--recursive|--force)", RegexOptions.Compiled)]
    private static partial Regex RmRfRegex();

    [GeneratedRegex(@"(?i)(rd|del|rmdir)\s+(/s|/f|/q)", RegexOptions.Compiled)]
    private static partial Regex WindowsDestructiveRegex();

    [GeneratedRegex(@"(?i)(format|diskpart|diskutil)\s*", RegexOptions.Compiled)]
    private static partial Regex DiskDestructiveRegex();

    [GeneratedRegex(@"(?i)shutdown\s+(/r|/s|/a)", RegexOptions.Compiled)]
    private static partial Regex ShutdownRegex();

    [GeneratedRegex(@"(?i)(mkfs|dd|chmod\s+777|chown\s+root)\s*", RegexOptions.Compiled)]
    private static partial Regex UnixDestructiveRegex();

    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(RunCommand, "ws_run_command", "Run a shell command in the workspace"),
    ];

    private static ResolvedWorkspace ResolveWorkspace(IServiceProvider sp, string? workspaceName = null)
    {
        var ctx = sp.GetRequiredService<AgentExecutionContext>();

        if (ctx.Workspace is not null && string.IsNullOrWhiteSpace(workspaceName))
            return ctx.Workspace;

        var repo = sp.GetRequiredService<WorkspaceRepository>();

        if (!string.IsNullOrWhiteSpace(workspaceName))
        {
            if (ctx.ActiveWorkspaceNames.Count > 0 && !ctx.ActiveWorkspaceNames.Contains(workspaceName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Workspace '{workspaceName}' is not active for this session. Use ws_list_active_workspaces to see available workspaces, or ws_set_active_workspaces to activate it.");

            var ws = repo.ResolveWorkspaceByName(ctx.AgentId, workspaceName).Result;
            if (ws is not null)
            {
                ctx.Workspace = ws;
                return ws;
            }
            throw new InvalidOperationException($"Workspace '{workspaceName}' not found for agent {ctx.AgentId}.");
        }

        if (ctx.Workspace is not null)
            return ctx.Workspace;

        var defaultWs = repo.ResolveDefaultWorkspace(ctx.AgentId).Result;

        if (defaultWs is null)
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            var defaultRoot = Path.Combine(appData, "SharpClaw", "workspaces", ctx.AgentId.ToString());
            var workspace = repo.UpsertWorkspace(
                $"agent-{ctx.AgentId}",
                defaultRoot,
                [],
                []).Result;
            var assignment = repo.UpsertAssignment(
                ctx.AgentId,
                workspace.Id,
                WorkspacePolicyMode.ConfirmWritesAndExec,
                true).Result;
            defaultWs = new ResolvedWorkspace
            {
                Workspace = workspace,
                PolicyMode = assignment.PolicyMode,
                IsDefault = assignment.IsDefault,
            };
        }

        ctx.Workspace = defaultWs;
        return defaultWs;
    }

    private static bool IsDestructiveCommand(string command)
    {
        if (RmRfRegex().IsMatch(command)) return true;
        if (WindowsDestructiveRegex().IsMatch(command)) return true;
        if (DiskDestructiveRegex().IsMatch(command)) return true;
        if (ShutdownRegex().IsMatch(command)) return true;
        if (UnixDestructiveRegex().IsMatch(command)) return true;
        return false;
    }

    private static ApprovalRiskLevel ClassifyRisk(string command)
    {
        if (IsDestructiveCommand(command))
            return ApprovalRiskLevel.Critical;

        if (command.Contains("sudo", StringComparison.OrdinalIgnoreCase)
            || command.Contains("chmod", StringComparison.OrdinalIgnoreCase)
            || command.Contains("chown", StringComparison.OrdinalIgnoreCase))
            return ApprovalRiskLevel.High;

        if (command.Contains("install", StringComparison.OrdinalIgnoreCase)
            || command.Contains("download", StringComparison.OrdinalIgnoreCase)
            || command.Contains("wget", StringComparison.OrdinalIgnoreCase)
            || command.Contains("curl", StringComparison.OrdinalIgnoreCase))
            return ApprovalRiskLevel.Medium;

        return ApprovalRiskLevel.Low;
    }

    private static ApprovalActionType ClassifyActionType(string command)
    {
        return IsDestructiveCommand(command)
            ? ApprovalActionType.DestructiveExecute
            : ApprovalActionType.Execute;
    }

    public static async Task<object> RunCommand(
        IServiceProvider sp,
        AIFunctionArguments args,
        string command,
        string? cwd = null,
        int? timeout_ms = null,
        string? approval_token = null,
        string? workspace = null)
    {
        var ws = ResolveWorkspace(sp, workspace);

        if (ws.PolicyMode is WorkspacePolicyMode.Disabled)
            return new { error = "Workspace is disabled." };

        if (ws.PolicyMode is WorkspacePolicyMode.ReadOnly)
            return new { error = "Command execution is not allowed in read-only mode." };

        var riskLevel = ClassifyRisk(command);
        var actionType = ClassifyActionType(command);
        var replayArgs = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["cwd"] = cwd,
            ["timeout_ms"] = timeout_ms,
            ["workspace"] = workspace,
        });
        var approvalCheck = await WorkspaceTools.CheckApprovalIfNeeded(
            sp,
            actionType,
            null,
            command,
            riskLevel,
            $"Run command: {command}",
            approval_token,
            workspace,
            WorkspaceTools.GetContextValue(args, "CallId"),
            "ws_run_command",
            replayArgs);

        if (approvalCheck is not null)
            return approvalCheck;

        var timeout = timeout_ms ?? DefaultTimeoutMs;
        timeout = Math.Min(timeout, 120_000);

        // Get the execution router and execute the command
        var routerFactory = sp.GetRequiredService<IWorkspaceExecutionRouterFactory>();
        var router = routerFactory.GetRouter(ws);

        return await router.RunCommand(ws, command, timeout, MaxOutputBytes);
    }

}
