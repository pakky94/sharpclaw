using Microsoft.Extensions.AI;

namespace SharpClaw.API.Agents.Tools;

public class DeferredTool
{
    public AIFunction Function { get; init; }

    public string Name => Function.Name;

    public DeferredTool(AIFunction function)
    {
        Function = function;
    }
}
