using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Agents.Tools.Workspace;

public static partial class CommandTools
{
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxOutputBytes = 50_000;

    private static readonly string[] DestructivePatterns = [
        @"rm\s+(-rf?|--recursive|--force)",
        @"rd\s+/s",
        @"del\s+(/f|/s|/q)",
        @"format\s+",
        @"diskpart",
        @"shutdown\s+(/r|/s)",
        @"rmdir\s+/s",
        @":\s*:\s*>\s*",
        @">\s*NUL",
        @"mkfs",
        @"dd\s+",
        @"chmod\s+777",
        @"chown\s+root",
        @"net\s+user",
        @"net\s+localgroup",
        @"reg\s+(delete|add)",
    ];

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
            var ws = repo.ResolveWorkspaceByName(ctx.AgentId, workspaceName).Result;
            if (ws is not null)
                return ws;
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

        var ctx = sp.GetRequiredService<AgentExecutionContext>();

        if (ws.PolicyMode is not WorkspacePolicyMode.TrueUnrestricted)
        {
            if (riskLevel >= ApprovalRiskLevel.Critical ||
                (ws.PolicyMode is WorkspacePolicyMode.Unrestricted && riskLevel >= ApprovalRiskLevel.High) ||
                (ws.PolicyMode is WorkspacePolicyMode.ConfirmWritesAndExec or WorkspacePolicyMode.ConfirmExecOnly))
            {
                if (string.IsNullOrWhiteSpace(approval_token))
                {
                    var approvalRepo = sp.GetRequiredService<WorkspaceRepository>();
                    var token = GenerateApprovalToken();
                    await approvalRepo.CreateApprovalEvent(
                        ctx.SessionId,
                        ctx.AgentId,
                        token,
                        actionType,
                        null,
                        command,
                        riskLevel);

                    return new
                    {
                        approval_required = true,
                        approval_token = token,
                        action = "execute",
                        command_preview = command.Length > 200 ? command[..200] + "..." : command,
                        risk = riskLevel.ToString().ToLowerInvariant(),
                        description = $"Run command: {command}",
                    };
                }

                var repo = sp.GetRequiredService<WorkspaceRepository>();
                var approval = await repo.GetApprovalEventByToken(approval_token);
                if (approval is null || approval.Status != ApprovalStatus.Approved)
                    return new { error = $"Invalid or unused approval token: {approval_token}" };
            }
        }

        var resolvedCwd = string.IsNullOrWhiteSpace(cwd) || cwd == "."
            ? ws.RootPath
            : PathContainment.NormalizePath(ws.RootPath, cwd);

        var timeout = timeout_ms ?? DefaultTimeoutMs;
        timeout = Math.Min(timeout, 120_000);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c \"{command}\"" : $"-c \"{command}\"",
                WorkingDirectory = resolvedCwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(); } catch { }
                return new
                {
                    title = $"Command timed out: {command}",
                    command,
                    timeout_ms = timeout,
                    error = $"Command exceeded {timeout}ms timeout and was terminated.",
                    killed = true,
                };
            }

            var stdout = await outputTask;
            var stderr = await errorTask;

            var stdoutBytes = Encoding.UTF8.GetByteCount(stdout);
            var stderrBytes = Encoding.UTF8.GetByteCount(stderr);

            if (stdoutBytes > MaxOutputBytes)
                stdout = stdout[..Math.Min(stdout.Length, MaxOutputBytes)] + "\n...[truncated]";

            if (stderrBytes > MaxOutputBytes)
                stderr = stderr[..Math.Min(stderr.Length, MaxOutputBytes)] + "\n...[truncated]";

            return new
            {
                title = $"Command executed: {command}",
                command,
                cwd = resolvedCwd,
                exit_code = process.ExitCode,
                @stdout = string.IsNullOrWhiteSpace(stdout) ? null : stdout,
                @stderr = string.IsNullOrWhiteSpace(stderr) ? null : stderr,
                duration_ms = (int)(process.ExitTime - process.StartTime).TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Command execution failed: {ex.Message}" };
        }
    }

    private static string GenerateApprovalToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "");
    }
}
