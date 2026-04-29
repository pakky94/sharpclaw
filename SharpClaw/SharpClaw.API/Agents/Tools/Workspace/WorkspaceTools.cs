using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database.Repositories;
using SharpClaw.API.Helpers;

namespace SharpClaw.API.Agents.Tools.Workspace;

public static class WorkspaceTools
{
    private const int MaxReadSize = 40_000;
    private const int MaxListEntries = 100;

    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(ListFiles, "ws_list_files", "List files and directories in the workspace. Optional workspace parameter selects a named workspace from the active session workspaces."),
        AIFunctionFactory.Create(ReadFile, "ws_read_file", "Read a file from the workspace. Optional workspace parameter selects a named workspace from the active session workspaces."),
        AIFunctionFactory.Create(WriteFile, "ws_write_file", "Write content to a file in the workspace. Optional workspace parameter selects a named workspace from the active session workspaces."),
        AIFunctionFactory.Create(EditFile, "ws_edit_file",
            """
            Performs exact string replacements in files. 
            
            Usage:
            - You must use your `Read` tool at least once in the conversation before editing. This tool will error if you attempt an edit without reading the file. 
            - When editing text from Read tool output, ensure you preserve the exact indentation (tabs/spaces) as it appears AFTER the line number prefix. The line number prefix format is: spaces + line number + tab. Everything after that tab is the actual file content to match. Never include any part of the line number prefix in the oldString or newString.
            - ALWAYS prefer editing existing files in the codebase. NEVER write new files unless explicitly required.
            - The edit will FAIL if `oldString` is not found in the file with an error "oldString not found in content".
            - The edit will FAIL if `oldString` is found multiple times in the file with an error "oldString found multiple times and requires more code context to uniquely identify the intended match". Either provide a larger string with more surrounding context to make it unique or use `replaceAll` to change every instance of `oldString`. 
            - Use `replaceAll` for replacing and renaming strings across the file. This parameter is useful if you want to rename a variable for instance.
            """ ),
        AIFunctionFactory.Create(DeleteFile, "ws_delete_file", "Delete a file or directory from the workspace. Optional workspace parameter selects a named workspace from the active session workspaces."),
        AIFunctionFactory.Create(MoveFile, "ws_move_file", "Move or rename a file or directory in the workspace. Optional workspace parameter selects a named workspace from the active session workspaces."),
        AIFunctionFactory.Create(MakeDirectory, "ws_make_directory", "Create a directory in the workspace. Optional workspace parameter selects a named workspace from the active session workspaces."),
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

    private static bool RequiresApproval(WorkspacePolicyMode mode, ApprovalActionType action)
    {
        return action switch
        {
            ApprovalActionType.Write or ApprovalActionType.Delete or ApprovalActionType.Move =>
                mode is WorkspacePolicyMode.ConfirmWritesAndExec or WorkspacePolicyMode.ConfirmExecOnly,
            ApprovalActionType.Execute or ApprovalActionType.DestructiveExecute =>
                mode is WorkspacePolicyMode.ConfirmWritesAndExec or WorkspacePolicyMode.ConfirmExecOnly or WorkspacePolicyMode.Unrestricted,
            _ => false,
        };
    }

    private static async Task<object?> CheckApprovalIfNeeded(
        IServiceProvider sp,
        ApprovalActionType actionType,
        string? targetPath,
        string? commandPreview,
        ApprovalRiskLevel riskLevel,
        string description,
        string? approvalToken,
        string? workspaceName = null)
    {
        var workspace = ResolveWorkspace(sp, workspaceName);

        if (workspace.PolicyMode is WorkspacePolicyMode.Disabled)
            return new { error = "Workspace is disabled." };

        if (workspace.PolicyMode is WorkspacePolicyMode.TrueUnrestricted)
            return null;

        if (workspace.PolicyMode is WorkspacePolicyMode.Unrestricted && actionType is ApprovalActionType.Write or ApprovalActionType.Delete or ApprovalActionType.Move)
            return null;

        if (workspace.PolicyMode is WorkspacePolicyMode.ReadOnly && actionType is not ApprovalActionType.Execute and not ApprovalActionType.DestructiveExecute)
            return null;

        if (!RequiresApproval(workspace.PolicyMode, actionType))
            return null;

        var runState = sp.GetService<AgentRunState>();
        var ctx = sp.GetRequiredService<AgentExecutionContext>();
        var repo = sp.GetRequiredService<WorkspaceRepository>();

        string token;
        if (!string.IsNullOrWhiteSpace(approvalToken))
        {
            token = approvalToken;
            var existing = await repo.GetApprovalEventByToken(token);
            if (existing is null)
                return new { error = $"Invalid approval token: {approvalToken}" };
            if (existing.Status == ApprovalStatus.Rejected)
                return new { error = $"Action was rejected by user: {description}" };
            if (existing.Status == ApprovalStatus.Expired)
                return new { error = $"Approval token expired: {approvalToken}" };
        }
        else
        {
            token = GenerateApprovalToken();
            await repo.CreateApprovalEvent(
                ctx.SessionId,
                ctx.AgentId,
                token,
                actionType,
                targetPath,
                commandPreview,
                riskLevel);
        }

        if (runState is null)
            return new { error = "Approval system not available." };

        var tcs = runState.CreateApprovalRequest(
            token,
            actionType.ToString().ToLowerInvariant(),
            targetPath,
            commandPreview,
            riskLevel.ToString().ToLowerInvariant(),
            description);

        bool approved;
        try
        {
            approved = await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            await repo.ResolveApprovalEvent(token, ApprovalStatus.Expired);
            return new { error = $"Approval timed out or was cancelled: {description}" };
        }

        await repo.ResolveApprovalEvent(token, approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected);

        if (!approved)
            return new { error = $"Action was rejected by user: {description}" };

        return null;
    }

    private static string GenerateApprovalToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "");
    }

    private static IWorkspaceExecutionRouter GetRouter(IServiceProvider sp, ResolvedWorkspace workspace)
    {
        var factory = sp.GetRequiredService<IWorkspaceExecutionRouterFactory>();
        return factory.GetRouter(workspace);
    }

    public static async Task<object> ListFiles(IServiceProvider sp, string path, bool recursive = false, bool include_hidden = false, string? workspace = null)
    {
        var ws = ResolveWorkspace(sp, workspace);

        if (ws.PolicyMode is WorkspacePolicyMode.Disabled)
            return new { error = "Workspace is disabled." };

        var router = GetRouter(sp, ws);
        return await router.ListFiles(ws, path, recursive, include_hidden);
    }

    public static async Task<object> ReadFile(IServiceProvider sp, string path,
        [Description("Number of lines to skip from the start of the file")] int? offset = null,
        [Description("Number of lines to read")] int? length = null,
        string? workspace = null)
    {
        var ws = ResolveWorkspace(sp, workspace);

        if (ws.PolicyMode is WorkspacePolicyMode.Disabled)
            return new { error = "Workspace is disabled." };

        // For large file handling with LCM artifacts, we still need to check file size locally
        // This is a special case that may need to be handled differently for bridge workspaces
        var router = GetRouter(sp, ws);
        var result = await router.ReadFile(ws, path, offset, length);

        // Handle large file truncation with LCM artifacts (local execution only for now)
        if (result is not IDictionary<string, object> resultDict)
            return result;

        // Check if we need to create an LCM artifact for large files
        if (!resultDict.ContainsKey("byte_count") || resultDict["byte_count"] is not int byteCount)
            return result;

        if (byteCount <= MaxReadSize)
            return result;

        // Large file handling - create LCM artifact (only for local for now)
        if (ws.Workspace.RuntimeKind == WorkspaceRuntimeKind.Local)
        {
            var ctx = sp.GetRequiredService<AgentExecutionContext>();
            var repo = sp.GetRequiredService<WorkspaceRepository>();
            var fileId = GenerateFileId(ws.Name, path, DateTime.UtcNow);

            // Get the content from result to store as artifact
            if (resultDict.TryGetValue("content", out var contentObj) && contentObj is string content)
            {
                var artifact = await repo.CreateLcmFileArtifact(
                    ctx.SessionId,
                    fileId,
                    "ws_read_file",
                    path,
                    byteCount,
                    null);

                return new
                {
                    title = $"File read (truncated): {path}",
                    file_id = fileId,
                    workspace_path = path,
                    byte_count = byteCount,
                    truncated = true,
                    content = content[..Math.Min(content.Length, MaxReadSize)],
                    note = $"File exceeds {MaxReadSize} bytes. Full content stored as artifact {fileId}.",
                };
            }
        }

        return result;
    }

    public static async Task<object> WriteFile(IServiceProvider sp, string path, string content, string mode = "overwrite", string? approval_token = null, string? workspace = null)
    {
        var approvalCheck = await CheckApprovalIfNeeded(
            sp,
            ApprovalActionType.Write,
            path,
            null,
            ApprovalRiskLevel.Medium,
            $"Write file '{path}' (mode: {mode})",
            approval_token,
            workspace);

        if (approvalCheck is not null)
            return approvalCheck;

        var ws = ResolveWorkspace(sp, workspace);
        var router = GetRouter(sp, ws);
        return await router.WriteFile(ws, path, content, mode);
    }

    public static async Task<object> EditFile(IServiceProvider sp, string path,
        [Description("The text to replace")] string oldString,
        [Description("The text to replace it with (must be different from oldString)")] string newString,
        [Description("Replace all occurrences of oldString (default false)")] bool replaceAll = false,
        string? approval_token = null, string? workspace = null)
    {
        var approvalCheck = await CheckApprovalIfNeeded(
            sp,
            ApprovalActionType.Write,
            path,
            null,
            ApprovalRiskLevel.Medium,
            $"Edit file '{path}'",
            approval_token,
            workspace);

        if (approvalCheck is not null)
            return approvalCheck;

        var ws = ResolveWorkspace(sp, workspace);
        var router = GetRouter(sp, ws);
        return await router.EditFile(ws, path, oldString, newString, replaceAll);
    }

    public static async Task<object> DeleteFile(IServiceProvider sp, string path, bool recursive = false, string? approval_token = null, string? workspace = null)
    {
        var approvalCheck = await CheckApprovalIfNeeded(
            sp,
            ApprovalActionType.Delete,
            path,
            null,
            recursive ? ApprovalRiskLevel.High : ApprovalRiskLevel.Medium,
            $"Delete {(recursive ? "directory" : "file")} '{path}'{(recursive ? " recursively" : "")}",
            approval_token,
            workspace);

        if (approvalCheck is not null)
            return approvalCheck;

        var ws = ResolveWorkspace(sp, workspace);
        var router = GetRouter(sp, ws);
        return await router.DeleteFile(ws, path, recursive);
    }

    public static async Task<object> MoveFile(IServiceProvider sp, string source, string destination, string? approval_token = null, string? workspace = null)
    {
        var approvalCheck = await CheckApprovalIfNeeded(
            sp,
            ApprovalActionType.Move,
            destination,
            null,
            ApprovalRiskLevel.Medium,
            $"Move '{source}' to '{destination}'",
            approval_token,
            workspace);

        if (approvalCheck is not null)
            return approvalCheck;

        var ws = ResolveWorkspace(sp, workspace);
        var router = GetRouter(sp, ws);
        return await router.MoveFile(ws, source, destination);
    }

    public static async Task<object> MakeDirectory(IServiceProvider sp, string path, string? approval_token = null, string? workspace = null)
    {
        var approvalCheck = await CheckApprovalIfNeeded(
            sp,
            ApprovalActionType.Write,
            path,
            null,
            ApprovalRiskLevel.Low,
            $"Create directory '{path}'",
            approval_token,
            workspace);

        if (approvalCheck is not null)
            return approvalCheck;

        var ws = ResolveWorkspace(sp, workspace);
        var router = GetRouter(sp, ws);
        return await router.MakeDirectory(ws, path);
    }

    private static string GenerateFileId(string workspaceName, string filePath, DateTime timestamp)
    {
        var pathBytes = Encoding.UTF8.GetBytes(filePath);
        var timestampBytes = BitConverter.GetBytes(timestamp.Ticks);
        var combinedBytes = pathBytes.Concat(timestampBytes).ToArray();
        var hashBytes = SHA256.HashData(combinedBytes);
        var encoder = new RadixEncoding(Constants.Alphabet);
        return $"sum_{encoder.Encode(hashBytes)[..16]}";
    }
}