namespace SharpClaw.API.Agents;

public class AgentClientServiceProvider(
    IServiceProvider serviceProvider,
    AgentExecutionContext context,
    AgentRunState? runState
) : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(AgentExecutionContext))
            return context;

        if (serviceType == typeof(AgentRunState))
            return runState;

        return serviceProvider.GetService(serviceType);
    }
}