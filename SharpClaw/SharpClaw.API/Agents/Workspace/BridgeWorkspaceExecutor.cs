using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Agents.Workspace;

public class BridgeWorkspaceExecutor : IWorkspaceExecutionRouter
{
    private readonly string _bridgeId;

    public BridgeWorkspaceExecutor(string bridgeId)
    {
        _bridgeId = bridgeId;
    }

    public Task<object> ListFiles(ResolvedWorkspace workspace, string path, bool recursive = false, bool includeHidden = false)
    {
        return Task.FromResult<object>(new
        {
            error = $"Bridge execution not yet implemented. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> ReadFile(ResolvedWorkspace workspace, string path, int? offset = null, int? length = null)
    {
        return Task.FromResult<object>(new
        {
            error = $"Bridge execution not yet implemented. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> WriteFile(ResolvedWorkspace workspace, string path, string content, string mode = "overwrite")
    {
        return Task.FromResult<object>(new
        {
            error = $"Bridge execution not yet implemented. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> EditFile(ResolvedWorkspace workspace, string path, string oldString, string newString, bool replaceAll = false)
    {
        return Task.FromResult<object>(new
        {
            error = $"Bridge execution not yet implemented. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> DeleteFile(ResolvedWorkspace workspace, string path, bool recursive = false)
    {
        return Task.FromResult<object>(new
        {
            error = $"Bridge execution not yet implemented. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> MoveFile(ResolvedWorkspace workspace, string source, string destination)
    {
        return Task.FromResult<object>(new
        {
            error = $"Bridge execution not yet implemented. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> MakeDirectory(ResolvedWorkspace workspace, string path)
    {
        return Task.FromResult<object>(new
        {
            error = $"Bridge execution not yet implemented. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }

    public Task<object> RunCommand(ResolvedWorkspace workspace, string command, int? timeoutMs = null, int? maxOutputBytes = null)
    {
        return Task.FromResult<object>(new
        {
            error = $"Bridge execution not yet implemented. Bridge ID: {_bridgeId}",
            status = "bridge_not_implemented"
        });
    }
}
