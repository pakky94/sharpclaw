using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database;

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

    private static string ResolvePath(IServiceProvider sp, string path, string? workspaceName = null)
    {
        var workspace = ResolveWorkspace(sp, workspaceName);
        return PathContainment.NormalizePath(workspace.RootPath, path);
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

    public static async Task<object> ListFiles(IServiceProvider sp, string path, bool recursive = false, bool include_hidden = false, string? workspace = null)
    {
        var ws = ResolveWorkspace(sp, workspace);

        if (ws.PolicyMode is WorkspacePolicyMode.Disabled)
            return new { error = "Workspace is disabled." };

        var resolvedPath = ResolvePath(sp, path, workspace);

        if (!Directory.Exists(resolvedPath))
            return new { error = $"Path does not exist: {path}" };

        var entries = new List<object>();
        var totalCount = 0;

        try
        {
            if (!recursive)
            {
                foreach (var dir in Directory.GetDirectories(resolvedPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (!include_hidden && (dirInfo.Attributes & FileAttributes.Hidden) != 0)
                        continue;
                    var relative = MakeRelative(ws.RootPath, dir);
                    entries.Add(new { type = "directory", path = relative, name = dirInfo.Name });
                }

                foreach (var file in Directory.GetFiles(resolvedPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var fileInfo = new FileInfo(file);
                    if (!include_hidden && (fileInfo.Attributes & FileAttributes.Hidden) != 0)
                        continue;
                    var relative = MakeRelative(ws.RootPath, file);
                    entries.Add(new { type = "file", path = relative, name = fileInfo.Name, size = fileInfo.Length });
                }
            }
            else
            {
                var queue = new Queue<string>();
                queue.Enqueue(resolvedPath);

                while (queue.Count > 0 && entries.Count < MaxListEntries)
                {
                    var current = queue.Dequeue();

                    foreach (var dir in Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (!include_hidden && (dirInfo.Attributes & FileAttributes.Hidden) != 0)
                            continue;
                        var relative = MakeRelative(ws.RootPath, dir);
                        entries.Add(new { type = "directory", path = relative, name = dirInfo.Name });
                        totalCount++;
                        if (entries.Count < MaxListEntries)
                            queue.Enqueue(dir);
                    }

                    foreach (var file in Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        var fileInfo = new FileInfo(file);
                        if (!include_hidden && (fileInfo.Attributes & FileAttributes.Hidden) != 0)
                            continue;
                        var relative = MakeRelative(ws.RootPath, file);
                        entries.Add(new { type = "file", path = relative, name = fileInfo.Name, size = fileInfo.Length });
                        totalCount++;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return new { error = $"Access denied: {ex.Message}" };
        }

        var truncated = recursive && totalCount > MaxListEntries;

        return new
        {
            title = $"Directory listing: {path}",
            path,
            recursive,
            entries,
            count = entries.Count,
            total_entries = totalCount,
            truncated,
            note = truncated ? $"Showing top {MaxListEntries} entries (breadth-first). {totalCount - MaxListEntries} more entries not shown." : null,
        };
    }

    public static async Task<object> ReadFile(IServiceProvider sp, string path,
        [Description("Number of lines to skip from the start of the file")] int? offset = null,
        [Description("Number of lines to read")] int? length = null,
        string? workspace = null)
    {
        var ws = ResolveWorkspace(sp, workspace);

        if (ws.PolicyMode is WorkspacePolicyMode.Disabled)
            return new { error = "Workspace is disabled." };

        var resolvedPath = ResolvePath(sp, path, workspace);

        if (!File.Exists(resolvedPath))
            return new { error = $"File does not exist: {path}" };

        try
        {
            // TODO: optimize reads for buffer
            var content = await File.ReadAllTextAsync(resolvedPath);
            if (offset.HasValue || length.HasValue)
            {
                var lines = content.Split('\n').Skip(offset ?? 0);
                if (length.HasValue)
                    lines = lines.Take(length.Value);
                content = string.Join('\n', lines);
            }

            var byteCount = Encoding.UTF8.GetByteCount(content);

            if (byteCount > MaxReadSize)
            {
                var ctx = sp.GetRequiredService<AgentExecutionContext>();
                var repo = sp.GetRequiredService<WorkspaceRepository>();
                var fileId = GenerateFileId(resolvedPath);

                var artifact = await repo.CreateLcmFileArtifact(
                    ctx.SessionId,
                    fileId,
                    "ws_read_file",
                    path,
                    byteCount,
                    resolvedPath);

                var snippet = content[..Math.Min(content.Length, MaxReadSize)];
                return new
                {
                    title = $"File read (truncated): {path}",
                    file_id = fileId,
                    workspace_path = path,
                    byte_count = byteCount,
                    truncated = true,
                    content = snippet,
                    note = $"File exceeds {MaxReadSize} bytes. Full content stored as artifact {fileId}.",
                };
            }

            return new
            {
                title = $"File read: {path}",
                path,
                byte_count = byteCount,
                content,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to read file: {ex.Message}" };
        }
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
        var resolvedPath = ResolvePath(sp, path, workspace);

        if (!PathContainment.IsPathAllowed(ws.RootPath, resolvedPath, ws.AllowlistPatterns, ws.DenylistPatterns))
            return new { error = $"Path is denied by workspace policy: {path}" };

        try
        {
            var parentDir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            FileMode fileMode;
            switch (mode)
            {
                case "append":
                    fileMode = FileMode.Append;
                    break;
                case "create_only":
                    if (File.Exists(resolvedPath))
                        return new { error = $"File already exists and mode is create_only: {path}" };
                    fileMode = FileMode.CreateNew;
                    break;
                default:
                    fileMode = FileMode.Create;
                    break;
            }

            await using var stream = new FileStream(resolvedPath, fileMode, FileAccess.Write, FileShare.None);
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes);

            return new
            {
                title = $"File written: {path}",
                path,
                mode,
                bytes_written = Encoding.UTF8.GetByteCount(content),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to write file: {ex.Message}" };
        }
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
        var resolvedPath = ResolvePath(sp, path, workspace);

        if (!PathContainment.IsPathAllowed(ws.RootPath, resolvedPath, ws.AllowlistPatterns, ws.DenylistPatterns))
            return new { error = $"Path is denied by workspace policy: {path}" };

        if (!File.Exists(resolvedPath))
            return new { error = $"File does not exist: {path}" };

        try
        {
            var oldFile = await File.ReadAllTextAsync(resolvedPath);

            var firstIdx = oldFile.IndexOf(oldString, StringComparison.Ordinal);

            if (firstIdx < 0)
                return new { error = $"oldString not found in file {path}" };

            if (!replaceAll)
            {
                var lastIdx = oldFile.LastIndexOf(oldString, StringComparison.Ordinal);
                if (lastIdx != firstIdx)
                    return new { error = $"multiple matches of oldString found in file {path}, to replace all occurrences call this tool with replaceAll=true" };
            }

            await File.WriteAllTextAsync(resolvedPath, oldFile.Replace(oldString, newString));

            return new
            {
                title = $"File edited: {path}",
                path,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to write file: {ex.Message}" };
        }
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
        var resolvedPath = ResolvePath(sp, path, workspace);

        if (!PathContainment.IsPathAllowed(ws.RootPath, resolvedPath, ws.AllowlistPatterns, ws.DenylistPatterns))
            return new { error = $"Path is denied by workspace policy: {path}" };

        try
        {
            if (Directory.Exists(resolvedPath))
            {
                if (!recursive)
                    return new { error = $"Path is a directory. Use recursive=true to delete: {path}" };
                Directory.Delete(resolvedPath, true);
            }
            else if (File.Exists(resolvedPath))
            {
                File.Delete(resolvedPath);
            }
            else
            {
                return new { error = $"Path does not exist: {path}" };
            }

            return new
            {
                title = $"Deleted: {path}",
                path,
                recursive,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to delete: {ex.Message}" };
        }
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
        var resolvedSource = ResolvePath(sp, source, workspace);
        var resolvedDest = ResolvePath(sp, destination, workspace);

        if (!PathContainment.IsPathAllowed(ws.RootPath, resolvedSource, ws.AllowlistPatterns, ws.DenylistPatterns))
            return new { error = $"Source path is denied by workspace policy: {source}" };

        if (!PathContainment.IsPathAllowed(ws.RootPath, resolvedDest, ws.AllowlistPatterns, ws.DenylistPatterns))
            return new { error = $"Destination path is denied by workspace policy: {destination}" };

        if (!File.Exists(resolvedSource) && !Directory.Exists(resolvedSource))
            return new { error = $"Source does not exist: {source}" };

        try
        {
            var parentDir = Path.GetDirectoryName(resolvedDest);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            if (Directory.Exists(resolvedSource))
                Directory.Move(resolvedSource, resolvedDest);
            else
                File.Move(resolvedSource, resolvedDest);

            return new
            {
                title = $"Moved: {source} -> {destination}",
                source,
                destination,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to move: {ex.Message}" };
        }
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
        var resolvedPath = ResolvePath(sp, path, workspace);

        if (!PathContainment.IsPathAllowed(ws.RootPath, resolvedPath, ws.AllowlistPatterns, ws.DenylistPatterns))
            return new { error = $"Path is denied by workspace policy: {path}" };

        try
        {
            if (Directory.Exists(resolvedPath))
                return new { error = $"Directory already exists: {path}" };

            Directory.CreateDirectory(resolvedPath);

            return new
            {
                title = $"Directory created: {path}",
                path,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to create directory: {ex.Message}" };
        }
    }

    private static string MakeRelative(string workspaceRoot, string fullPath)
    {
        var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(fullPath);

        if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedPath[normalizedRoot.Length..].TrimStart(Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? "." : relative;
        }

        return normalizedPath;
    }

    private static string GenerateFileId(string filePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath));
        var hex = Convert.ToHexStringLower(hash);
        return $"file_{hex[..16]}";
    }
}