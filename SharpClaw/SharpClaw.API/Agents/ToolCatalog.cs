using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Tools.Fragments;
using SharpClaw.API.Agents.Tools.Lcm;
using SharpClaw.API.Agents.Tools.Tasks;
using SharpClaw.API.Agents.Tools.Web;
using SharpClaw.API.Agents.Tools.Workspace;

namespace SharpClaw.API.Agents;

public static class ToolCatalog
{
    public static List<AIFunction> BuildTools() =>
    [
        ..FragmentTools.Functions,
        ..LcmTools.Functions,
        ..WorkspaceTools.Functions,
        ..CommandTools.Functions,
        ..SessionWorkspaceTools.Functions,
        ..WebTools.Functions,
        TasksTools.TaskTool([("Main", "the main agent")]), // TODO: get these agents from where?
    ];
}
