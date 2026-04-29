using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Agents.Workspace;

public class BridgeWorkspaceExecutor : IWorkspaceExecutionRouter
{
    private readonly string _bridgeId;
    private readonly BridgeConnectionManager _connectionManager;
    private readonly ILogger<BridgeWorkspaceExecutor> _logger;

    public BridgeWorkspaceExecutor(string bridgeId, BridgeConnectionManager connectionManager, ILogger<BridgeWorkspaceExecutor> logger)
    {
        _bridgeId = bridgeId;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<object> ListFiles(ResolvedWorkspace workspace, string path, bool recursive = false, bool includeHidden = false)
    {
        var request = new BridgeRequest
        {
            SessionId = Guid.NewGuid(),
            WorkspaceName = workspace.Name,
            Operation = "list_files",
            Args = new Dictionary<string, object?>
            {
                { "path", path },
                { "recursive", recursive },
                { "include_hidden", includeHidden },
            },
            PolicyContext = new BridgePolicyContext
            {
                AllowlistPatterns = workspace.AllowlistPatterns,
                DenylistPatterns = workspace.DenylistPatterns,
                PolicyMode = workspace.PolicyMode.ToString().ToLowerInvariant(),
                RootPath = workspace.RootPath,
            }
        };

        var response = await _connectionManager.SendRequest(_bridgeId, request);

        if (response.Status != "ok")
            return new { error = response.ErrorMessage ?? "Bridge execution failed.", status = response.Status };

        return response.Result ?? new { };
    }

    public async Task<object> ReadFile(ResolvedWorkspace workspace, string path, int? offset = null, int? length = null)
    {
        var request = new BridgeRequest
        {
            SessionId = Guid.NewGuid(),
            WorkspaceName = workspace.Name,
            Operation = "read_file",
            Args = new Dictionary<string, object?>
            {
                { "path", path },
                { "offset", offset },
                { "length", length },
            },
            PolicyContext = new BridgePolicyContext
            {
                AllowlistPatterns = workspace.AllowlistPatterns,
                DenylistPatterns = workspace.DenylistPatterns,
                PolicyMode = workspace.PolicyMode.ToString().ToLowerInvariant(),
                RootPath = workspace.RootPath,
            }
        };

        var response = await _connectionManager.SendRequest(_bridgeId, request);

        if (response.Status != "ok")
            return new { error = response.ErrorMessage ?? "Bridge execution failed.", status = response.Status };

        return response.Result ?? new { };
    }

    public async Task<object> WriteFile(ResolvedWorkspace workspace, string path, string content, string mode = "overwrite")
    {
        var request = new BridgeRequest
        {
            SessionId = Guid.NewGuid(),
            WorkspaceName = workspace.Name,
            Operation = "write_file",
            Args = new Dictionary<string, object?>
            {
                { "path", path },
                { "content", content },
                { "mode", mode },
            },
            PolicyContext = new BridgePolicyContext
            {
                AllowlistPatterns = workspace.AllowlistPatterns,
                DenylistPatterns = workspace.DenylistPatterns,
                PolicyMode = workspace.PolicyMode.ToString().ToLowerInvariant(),
                RootPath = workspace.RootPath,
            }
        };

        var response = await _connectionManager.SendRequest(_bridgeId, request);

        if (response.Status != "ok")
            return new { error = response.ErrorMessage ?? "Bridge execution failed.", status = response.Status };

        return response.Result ?? new { };
    }

    public async Task<object> EditFile(ResolvedWorkspace workspace, string path, string oldString, string newString, bool replaceAll = false)
    {
        var request = new BridgeRequest
        {
            SessionId = Guid.NewGuid(),
            WorkspaceName = workspace.Name,
            Operation = "edit_file",
            Args = new Dictionary<string, object?>
            {
                { "path", path },
                { "oldString", oldString },
                { "newString", newString },
                { "replaceAll", replaceAll },
            },
            PolicyContext = new BridgePolicyContext
            {
                AllowlistPatterns = workspace.AllowlistPatterns,
                DenylistPatterns = workspace.DenylistPatterns,
                PolicyMode = workspace.PolicyMode.ToString().ToLowerInvariant(),
                RootPath = workspace.RootPath,
            }
        };

        var response = await _connectionManager.SendRequest(_bridgeId, request);

        if (response.Status != "ok")
            return new { error = response.ErrorMessage ?? "Bridge execution failed.", status = response.Status };

        return response.Result ?? new { };
    }

    public async Task<object> DeleteFile(ResolvedWorkspace workspace, string path, bool recursive = false)
    {
        var request = new BridgeRequest
        {
            SessionId = Guid.NewGuid(),
            WorkspaceName = workspace.Name,
            Operation = "delete_file",
            Args = new Dictionary<string, object?>
            {
                { "path", path },
                { "recursive", recursive },
            },
            PolicyContext = new BridgePolicyContext
            {
                AllowlistPatterns = workspace.AllowlistPatterns,
                DenylistPatterns = workspace.DenylistPatterns,
                PolicyMode = workspace.PolicyMode.ToString().ToLowerInvariant(),
                RootPath = workspace.RootPath,
            }
        };

        var response = await _connectionManager.SendRequest(_bridgeId, request);

        if (response.Status != "ok")
            return new { error = response.ErrorMessage ?? "Bridge execution failed.", status = response.Status };

        return response.Result ?? new { };
    }

    public async Task<object> MoveFile(ResolvedWorkspace workspace, string source, string destination)
    {
        var request = new BridgeRequest
        {
            SessionId = Guid.NewGuid(),
            WorkspaceName = workspace.Name,
            Operation = "move_file",
            Args = new Dictionary<string, object?>
            {
                { "source", source },
                { "destination", destination },
            },
            PolicyContext = new BridgePolicyContext
            {
                AllowlistPatterns = workspace.AllowlistPatterns,
                DenylistPatterns = workspace.DenylistPatterns,
                PolicyMode = workspace.PolicyMode.ToString().ToLowerInvariant(),
                RootPath = workspace.RootPath,
            }
        };

        var response = await _connectionManager.SendRequest(_bridgeId, request);

        if (response.Status != "ok")
            return new { error = response.ErrorMessage ?? "Bridge execution failed.", status = response.Status };

        return response.Result ?? new { };
    }

    public async Task<object> MakeDirectory(ResolvedWorkspace workspace, string path)
    {
        var request = new BridgeRequest
        {
            SessionId = Guid.NewGuid(),
            WorkspaceName = workspace.Name,
            Operation = "make_directory",
            Args = new Dictionary<string, object?>
            {
                { "path", path },
            },
            PolicyContext = new BridgePolicyContext
            {
                AllowlistPatterns = workspace.AllowlistPatterns,
                DenylistPatterns = workspace.DenylistPatterns,
                PolicyMode = workspace.PolicyMode.ToString().ToLowerInvariant(),
                RootPath = workspace.RootPath,
            }
        };

        var response = await _connectionManager.SendRequest(_bridgeId, request);

        if (response.Status != "ok")
            return new { error = response.ErrorMessage ?? "Bridge execution failed.", status = response.Status };

        return response.Result ?? new { };
    }

    public async Task<object> RunCommand(ResolvedWorkspace workspace, string command, int? timeoutMs = null, int? maxOutputBytes = null)
    {
        var request = new BridgeRequest
        {
            SessionId = Guid.NewGuid(),
            WorkspaceName = workspace.Name,
            Operation = "run_command",
            Args = new Dictionary<string, object?>
            {
                { "command", command },
                { "timeout_ms", timeoutMs },
                { "max_output_bytes", maxOutputBytes },
                { "cwd", "." }, // Default to workspace root
            },
            PolicyContext = new BridgePolicyContext
            {
                AllowlistPatterns = workspace.AllowlistPatterns,
                DenylistPatterns = workspace.DenylistPatterns,
                PolicyMode = workspace.PolicyMode.ToString().ToLowerInvariant(),
                RootPath = workspace.RootPath,
            }
        };

        var response = await _connectionManager.SendRequest(_bridgeId, request);

        if (response.Status != "ok")
            return new { error = response.ErrorMessage ?? "Bridge execution failed.", status = response.Status };

        return response.Result ?? new { };
    }
}
