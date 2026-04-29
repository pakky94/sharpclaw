using Microsoft.Extensions.Logging;
using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Agents.Workspace;

public interface IWorkspaceExecutionRouterFactory
{
    IWorkspaceExecutionRouter GetRouter(ResolvedWorkspace workspace);
}

public class WorkspaceExecutionRouterFactory : IWorkspaceExecutionRouterFactory
{
    private readonly LocalWorkspaceExecutor _localExecutor;
    private readonly BridgeConnectionManager _connectionManager;
    private readonly ILogger<WorkspaceExecutionRouterFactory> _logger;

    public WorkspaceExecutionRouterFactory(
        LocalWorkspaceExecutor localExecutor,
        BridgeConnectionManager connectionManager,
        ILogger<WorkspaceExecutionRouterFactory> logger)
    {
        _localExecutor = localExecutor;
        _connectionManager = connectionManager;
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
        
        // Check if bridge is connected
        var bridges = _connectionManager.GetConnectedBridges();
        var bridge = bridges.FirstOrDefault(b => b.BridgeId == bridgeId);
        
        if (bridge is null)
        {
            _logger.LogWarning("Bridge {BridgeId} not connected. Workspace: {WorkspaceName}", 
                bridgeId, workspace.Name);
            // Return executor that returns proper error for offline bridge
            return new BridgeWorkspaceExecutor(bridgeId, _connectionManager, 
                _logger as ILogger<BridgeWorkspaceExecutor> ?? 
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BridgeWorkspaceExecutor>.Instance);
        }
        
        return new BridgeWorkspaceExecutor(bridgeId, _connectionManager, 
            _logger as ILogger<BridgeWorkspaceExecutor> ?? 
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BridgeWorkspaceExecutor>.Instance);
    }
}
