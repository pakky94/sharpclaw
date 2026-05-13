using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Channels;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Agents.Tools;

public static class SendMessageTool
{
    public static readonly AIFunction Function =
        AIFunctionFactory.Create(SendMessage, "send_message",
            "Send a message to a session by reference (format: session:{agentName}:{tag}). " +
            "The message is delivered to all channels connected to that session. " +
            "This is fire-and-forget — no agent run is triggered in the target session.");

    private static async Task<object> SendMessage(
        IServiceProvider serviceProvider,
        string session_ref,
        string text)
    {
        var agent = serviceProvider.GetRequiredService<Agent>();
        var agentsRepo = serviceProvider.GetRequiredService<AgentsRepository>();
        var channelRouter = serviceProvider.GetRequiredService<ChannelRouter>();

        // Parse session_ref: "session:{agentName}:{tag}"
        var parts = session_ref.Split(':');
        if (parts.Length != 3 || parts[0] != "session")
        {
            return new { error = $"Invalid session reference: '{session_ref}'. Format: session:{{agentName}}:{{tag}}" };
        }

        var agentName = parts[1];
        var tag = parts[2];

        // Find the agent by name
        var allAgents = await agentsRepo.GetAgents();
        var targetAgent = allAgents.FirstOrDefault(a =>
            string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));

        if (targetAgent is null)
        {
            return new { error = $"Agent '{agentName}' not found." };
        }

        // Find or create the session by tag
        var sessionId = await agent.GetSessionByTag(targetAgent.Id, tag);
        if (sessionId is null)
        {
            // Create a new session with this tag
            sessionId = await agent.CreateSession(
                agentId: targetAgent.Id,
                name: $"{agentName}:{tag}",
                visibleInSidebar: true,
                tag: tag);
        }

        // Post the notification
        await agent.PostNotification(sessionId.Value, text);

        // Broadcast to all connected channels
        await channelRouter.BroadcastNewMessages(sessionId.Value);

        return new
        {
            success = true,
            session_ref = session_ref,
            session_id = sessionId.Value,
        };
    }
}
