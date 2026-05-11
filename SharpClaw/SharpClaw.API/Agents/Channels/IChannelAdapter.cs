using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Agents.Channels;

/// <summary>
/// Adapter that translates between an external platform (Discord, Telegram, etc.)
/// and SharpClaw's internal message model.
/// </summary>
public interface IChannelAdapter
{
    /// <summary>
    /// The channel type this adapter handles (e.g. "discord", "telegram").
    /// </summary>
    string ChannelType { get; }

    /// <summary>
    /// Called once at startup with all enabled channels of this type.
    /// The adapter should connect to the platform and start listening for messages.
    /// When a message arrives, call <paramref name="onMessage"/> with (channel, identityId, text).
    /// </summary>
    Task StartAsync(
        IEnumerable<Channel> channels,
        Func<Channel, string, string, Task> onMessage);

    /// <summary>
    /// Called when a channel is disabled or the app shuts down.
    /// </summary>
    Task StopAsync(Channel channel);

    /// <summary>
    /// Send a text message to a specific identity on this channel.
    /// </summary>
    Task SendMessageAsync(Channel channel, string identityId, string text);

    /// <summary>
    /// Send an approval request with interactive Approve/Reject controls.
    /// Returns true if the platform supports approvals and the message was sent.
    /// </summary>
    Task<bool> SendApprovalAsync(Channel channel, string identityId, WorkspaceApprovalEvent approval);
}
