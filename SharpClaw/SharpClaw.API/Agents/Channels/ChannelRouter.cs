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
            // 1. Resolve or create session
            var sessionId = await ResolveSession(channel, identityId);

            // 2. Enqueue the message — this starts the agent run
            _ = await _agent.EnqueueMessage(sessionId, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing channel message from {ChannelType}:{ChannelId} identity={IdentityId}",
                channel.Type, channel.Id, identityId);

            // Try to send error back to the user
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

    private async Task BroadcastNewMessages(Guid sessionId)
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