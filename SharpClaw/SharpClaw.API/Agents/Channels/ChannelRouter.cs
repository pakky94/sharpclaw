using Microsoft.Extensions.AI;
using SharpClaw.API.Database.Repositories;
using SharpClaw.API.Helpers;

namespace SharpClaw.API.Agents.Channels;

/// <summary>
/// Routes messages between external channels and agent sessions.
/// Owns the mapping of (channel, identity) → session.
/// After each run completes, broadcasts new messages to all channels connected to that session.
/// </summary>
public class ChannelRouter
{
    private readonly ChannelRepository _channelRepository;
    private readonly ChatRepository _chatRepository;
    private readonly Agent _agent;
    private readonly ILogger<ChannelRouter> _logger;
    private readonly Dictionary<string, IChannelAdapter> _adapters = new();

    public ChannelRouter(
        ChannelRepository channelRepository,
        ChatRepository chatRepository,
        Agent agent,
        ILogger<ChannelRouter> logger)
    {
        _channelRepository = channelRepository;
        _chatRepository = chatRepository;
        _agent = agent;
        _logger = logger;

        _agent.OnTurnCompleted += async (_, args) =>
        {
            await BroadcastNewMessages(args.SessionId);
        };
    }

    public void RegisterAdapter(IChannelAdapter adapter)
    {
        _adapters[adapter.ChannelType] = adapter;
    }

    /// <summary>
    /// Start all enabled channels by delegating to their respective adapters.
    /// </summary>
    public async Task StartAsync()
    {
        var allChannels = await _channelRepository.GetAll();
        var enabledChannels = allChannels.Where(c => c.Enabled).ToArray();

        foreach (var group in enabledChannels.GroupBy(c => c.Type))
        {
            if (_adapters.TryGetValue(group.Key, out var adapter))
            {
                await adapter.StartAsync(group, OnMessageAsync);
                _logger.LogInformation("Started {Count} {Type} channel(s)", group.Count(), group.Key);
            }
            else
            {
                _logger.LogWarning("No adapter registered for channel type '{Type}'", group.Key);
            }
        }
    }

    /// <summary>
    /// Called by adapters when a message arrives from an external platform.
    /// </summary>
    public async Task OnMessageAsync(Channel channel, string identityId, string text)
    {
        try
        {
            // Intercept commands
            if (text.StartsWith('/'))
            {
                await HandleCommand(channel, identityId, text);
                return;
            }

            // 1. Resolve or create session
            var sessionId = await ResolveSession(channel, identityId);

            // 2. Enqueue the message — this starts the agent run
            _ = await _agent.EnqueueMessage(sessionId, text);
            // var run = await _agent.EnqueueMessage(sessionId, text);
            //
            // // 3. Wait for the run to complete
            // await WaitForRunCompletion(sessionId, run);
            //
            // // 4. Broadcast new messages to all connected channels
            // await BroadcastNewMessages(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing channel message from {ChannelType}:{ChannelId} identity={IdentityId}",
                channel.Type, channel.Id, identityId);

            try
            {
                if (_adapters.TryGetValue(channel.Type, out var adapter))
                {
                    await adapter.SendMessageAsync(channel, identityId,
                        $"Sorry, something went wrong: {ex.Message}");
                }
            }
            catch
            {
                // Best effort
            }
        }
    }

    private async Task<Guid> ResolveSession(Channel channel, string identityId)
    {
        // Check if this specific identity already has a session
        var existingMapping = await _channelRepository.GetChannelSession(channel.Id, identityId);
        if (existingMapping is not null)
            return existingMapping.session_id;

        // Default binding: try to find a "main" tagged session for this agent
        var mainSessionId = await _agent.GetSessionByTag(channel.AgentId, "main");
        if (mainSessionId is not null)
        {
            await _channelRepository.CreateChannelSession(channel.Id, identityId, mainSessionId.Value);
            return mainSessionId.Value;
        }

        if (channel.RoutingMode == "shared")
        {
            // Shared mode: check if any session already exists for this channel
            var anyMapping = await _channelRepository.GetAnyChannelSession(channel.Id);
            if (anyMapping is not null)
            {
                // Reuse the existing session, but create a row for this identity
                // so responses can be delivered back to the right Discord channel
                await _channelRepository.CreateChannelSession(channel.Id, identityId, anyMapping.session_id);
                return anyMapping.session_id;
            }
        }

        // No existing session — create a new one
        var sessionName = channel.RoutingMode == "per_user"
            ? $"{channel.Type}: {identityId}"
            : $"{channel.Type}: {channel.Name}";

        var sessionId = await _agent.CreateSession(
            agentId: channel.AgentId,
            name: sessionName,
            visibleInSidebar: true);

        await _channelRepository.CreateChannelSession(channel.Id, identityId, sessionId);
        return sessionId;
    }

    private async Task HandleCommand(Channel channel, string identityId, string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var args = parts.Length > 1 ? parts[1] : "";

        if (!_adapters.TryGetValue(channel.Type, out var adapter))
            return;

        switch (command)
        {
            case "/select":
                await HandleSelect(channel, identityId, args, adapter);
                break;
            case "/new":
                await HandleNew(channel, identityId, args, adapter);
                break;
            case "/sessions":
                await HandleSessions(channel, identityId, adapter);
                break;
            case "/help":
                await adapter.SendMessageAsync(channel, identityId,
                    "**Available commands:**\n" +
                    "`/select {tag}` — Switch to a tagged session\n" +
                    "`/new [{tag}] [{name}]` — Create a new session (optionally tagged)\n" +
                    "`/sessions` — List tagged sessions\n" +
                    "`/help` — Show this help");
                break;
            default:
                await adapter.SendMessageAsync(channel, identityId,
                    $"Unknown command: `{command}`. Type `/help` for available commands.");
                break;
        }
    }

    private async Task HandleSelect(Channel channel, string identityId, string args, IChannelAdapter adapter)
    {
        var tag = args.Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            await adapter.SendMessageAsync(channel, identityId,
                "Usage: `/select {tag}` — e.g. `/select main`");
            return;
        }

        var sessionId = await _agent.GetSessionByTag(channel.AgentId, tag);
        if (sessionId is null)
        {
            await adapter.SendMessageAsync(channel, identityId,
                $"No session found with tag `{tag}`. Use `/sessions` to see available sessions.");
            return;
        }

        // Get the current max sequence of the target session so we only
        // broadcast messages generated after the switch, not historical ones
        var maxSeq = await _chatRepository.GetMaxSequence(sessionId.Value);

        // Update the channel_sessions mapping to point to the new session
        var existing = await _channelRepository.GetChannelSession(channel.Id, identityId);
        if (existing is not null)
        {
            await _channelRepository.UpdateChannelSessionSession(channel.Id, identityId, sessionId.Value, maxSeq);
        }
        else
        {
            await _channelRepository.CreateChannelSession(channel.Id, identityId, sessionId.Value);
        }

        await adapter.SendMessageAsync(channel, identityId,
            $"Switched to session `{tag}`.");
    }

    private async Task HandleNew(Channel channel, string identityId, string args, IChannelAdapter adapter)
    {
        var argParts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        string? tag = null;
        string? name = null;

        // Get the current session to inherit its tag
        var existing = await _channelRepository.GetChannelSession(channel.Id, identityId);
        PersistedSession? currentSession = null;
        if (existing is not null)
            currentSession = await _chatRepository.GetSession(existing.session_id);

        if (argParts.Length == 0)
        {
            // /new — inherit current tag, old tag cleared
            tag = currentSession?.Tag;
        }
        else if (argParts.Length == 1)
        {
            // /new {name} — inherit current tag, use arg as name
            name = argParts[0];
            tag = currentSession?.Tag;
        }
        else
        {
            // /new {tag} {name} — explicit tag
            tag = argParts[0];
            name = argParts[1];
        }

        // Unlink the current session's tag so it can be reused
        if (existing is not null)
            await _agent.UnlinkSessionTag(existing.session_id);

        var sessionId = await _agent.CreateSession(
            agentId: channel.AgentId,
            name: name,
            visibleInSidebar: true,
            tag: tag);

        // Update or create the channel_sessions mapping
        // New session has no messages, so cursor starts at 0
        if (existing is not null)
            await _channelRepository.UpdateChannelSessionSession(channel.Id, identityId, sessionId, lastBroadcastSequence: 0);
        else
            await _channelRepository.CreateChannelSession(channel.Id, identityId, sessionId);

        var displayName = tag ?? "untitled";
        await adapter.SendMessageAsync(channel, identityId,
            $"Created new session `{displayName}`{(name is not null ? $" ({name})" : "")}.");
    }

    private async Task HandleSessions(Channel channel, string identityId, IChannelAdapter adapter)
    {
        var sessions = await _agent.GetTaggedSessions(channel.AgentId);

        if (sessions.Count == 0)
        {
            await adapter.SendMessageAsync(channel, identityId,
                "No tagged sessions. Use `/new {tag}` to create one.");
            return;
        }

        var lines = new List<string> { "**Tagged sessions:**" };
        foreach (var s in sessions)
        {
            var displayName = s.Name ?? "—";
            lines.Add($"• `{s.Tag}` — {displayName}");
        }

        await adapter.SendMessageAsync(channel, identityId, string.Join('\n', lines));
    }

    private static async Task WaitForRunCompletion(Guid sessionId, AgentRunState run)
    {
        while (run.Status is AgentRunStatus.Pending or AgentRunStatus.Running or AgentRunStatus.Waiting)
        {
            await Task.Delay(200);
        }
    }

    public async Task BroadcastNewMessages(Guid sessionId)
    {
        try
        {
            var connectedChannels = await _channelRepository.GetConnectedChannelsForSession(sessionId);

            foreach (var info in connectedChannels)
            {
                if (!_adapters.TryGetValue(info.channel_type, out var adapter))
                    continue;

                try
                {
                    var rawMessages = await _chatRepository.LoadRawMessages(sessionId);

                    // Find messages with a sequence number beyond this channel's cursor
                    var newMessages = rawMessages
                        .Where(m =>
                        {
                            var seq = m.Response.AdditionalProperties?[Constants.SequenceIdKey] as long?;
                            return seq.HasValue && seq.Value > info.last_broadcast_sequence;
                        })
                        .ToList();

                    foreach (var raw in newMessages)
                    {
                        foreach (var message in raw.Response.Messages)
                        {
                            // Only broadcast assistant responses — never echo user messages
                            if (message.Role != ChatRole.Assistant)
                                continue;

                            if (!string.IsNullOrWhiteSpace(message.Text))
                            {
                                await adapter.SendMessageAsync(
                                    new Channel
                                    {
                                        Id = info.channel_id,
                                        Type = info.channel_type,
                                        Config = info.channel_config,
                                        AgentId = info.agent_id,
                                    },
                                    info.identity_id,
                                    message.Text);
                            }
                        }
                    }

                    // Advance the broadcast cursor to the latest sequence we've seen
                    var latestSeq = rawMessages
                        .Select(m => m.Response.AdditionalProperties?[Constants.SequenceIdKey] as long?)
                        .Where(s => s.HasValue)
                        .Select(s => s!.Value)
                        .DefaultIfEmpty(info.last_broadcast_sequence)
                        .Max();

                    if (latestSeq > info.last_broadcast_sequence)
                    {
                        await _channelRepository.UpdateBroadcastCursor(
                            info.channel_id, info.identity_id, latestSeq);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error broadcasting to channel {ChannelId} identity {IdentityId}",
                        info.channel_id, info.identity_id);
                }
            }
        } catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting new messages");
        }
    }
}
