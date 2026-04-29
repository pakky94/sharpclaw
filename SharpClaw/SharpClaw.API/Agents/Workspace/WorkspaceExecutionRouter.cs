using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Agents.Workspace;

public interface IWorkspaceExecutionRouterFactory
{
    IWorkspaceExecutionRouter GetRouter(ResolvedWorkspace workspace);
}

public class WorkspaceExecutionRouterFactory : IWorkspaceExecutionRouterFactory
{
    private readonly LocalWorkspaceExecutor _localExecutor;
    private readonly ILogger<WorkspaceExecutionRouterFactory> _logger;

    public WorkspaceExecutionRouterFactory(LocalWorkspaceExecutor localExecutor, ILogger<WorkspaceExecutionRouterFactory> logger)
    {
        _localExecutor = localExecutor;
        _logger = logger;
    }

    public IWorkspaceExecutionRouter GetRouter(ResolvedWorkspace workspace)
    {
        return workspace.Workspace.RuntimeKind switch
        {
            WorkspaceRuntimeKind.Local => _localExecutor,
            WorkspaceRuntimeKind.Bridge => CreateBridgeExecutor(workspace),
            _ => _localExecutor,
        };
    }

    private IWorkspaceExecutionRouter CreateBridgeExecutor(ResolvedWorkspace workspace)
    {
        var bridgeId = workspace.Workspace.RuntimeTarget ?? "unknown";
        _logger.LogWarning("Bridge execution requested but not yet implemented. Bridge ID: {BridgeId}, Workspace: {WorkspaceName}", 
            bridgeId, workspace.Name);
        
        // For now, return a stub that returns not-implemented errors
        // In later phases, this will look up the bridge connection and return a real executor
        return new BridgeWorkspaceExecutor(bridgeId);
    }
}
