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

    public Task<object> WriteFile(ResolvedWorkspace workspace, string path, string content, string mode = "overwrite")
    {
        _logger.LogWarning("WriteFile not yet implemented for bridge. Bridge ID: {BridgeId}", _bridgeId);
        return Task.FromResult<object>(new
        {
            error = $"Write operations not yet implemented for bridge. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> EditFile(ResolvedWorkspace workspace, string path, string oldString, string newString, bool replaceAll = false)
    {
        _logger.LogWarning("EditFile not yet implemented for bridge. Bridge ID: {BridgeId}", _bridgeId);
        return Task.FromResult<object>(new
        {
            error = $"Edit operations not yet implemented for bridge. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> DeleteFile(ResolvedWorkspace workspace, string path, bool recursive = false)
    {
        _logger.LogWarning("DeleteFile not yet implemented for bridge. Bridge ID: {BridgeId}", _bridgeId);
        return Task.FromResult<object>(new
        {
            error = $"Delete operations not yet implemented for bridge. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> MoveFile(ResolvedWorkspace workspace, string source, string destination)
    {
        _logger.LogWarning("MoveFile not yet implemented for bridge. Bridge ID: {BridgeId}", _bridgeId);
        return Task.FromResult<object>(new
        {
            error = $"Move operations not yet implemented for bridge. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> MakeDirectory(ResolvedWorkspace workspace, string path)
    {
        _logger.LogWarning("MakeDirectory not yet implemented for bridge. Bridge ID: {BridgeId}", _bridgeId);
        return Task.FromResult<object>(new
        {
            error = $"Make directory operations not yet implemented for bridge. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> RunCommand(ResolvedWorkspace workspace, string command, int? timeoutMs = null, int? maxOutputBytes = null)
    {
        _logger.LogWarning("RunCommand not yet implemented for bridge. Bridge ID: {BridgeId}", _bridgeId);
        return Task.FromResult<object>(new
        {
            error = $"Command execution not yet implemented for bridge. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }
}
