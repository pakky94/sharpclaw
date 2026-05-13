using Discord;
using Discord.WebSocket;
using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Agents.Channels.Discord;

public class DiscordAdapter : IChannelAdapter
{
    private readonly ILogger<DiscordAdapter> _logger;
    private readonly Dictionary<string, DiscordSocketClient> _clientsByToken = new();
    private readonly Dictionary<ulong, Channel> _channelsByDiscordChannelId = new();
    private readonly Dictionary<string, int> _tokenRefCounts = new();

    public string ChannelType => "discord";

    public DiscordAdapter(ILogger<DiscordAdapter> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(
        IEnumerable<Channel> channels,
        Func<Channel, string, string, Task> onMessage)
    {
        var channelsList = channels.ToList();

        foreach (var channel in channelsList)
        {
            try
            {
                var config = DiscordConfig.FromJson(channel.Config);
                if (string.IsNullOrWhiteSpace(config.BotToken))
                {
                    _logger.LogWarning("Discord channel '{Name}' has no bot token, skipping", channel.Name);
                    continue;
                }

                if (!_clientsByToken.ContainsKey(config.BotToken))
                {
                    var client = new DiscordSocketClient(new DiscordSocketConfig
                    {
                        GatewayIntents = GatewayIntents.DirectMessages
                                         | GatewayIntents.GuildMessages
                                         | GatewayIntents.MessageContent,
                    });

                    client.MessageReceived += async (SocketMessage message) =>
                    {
                        await HandleMessageAsync(message, onMessage);
                    };

                    client.ButtonExecuted += async (SocketMessageComponent component) =>
                    {
                        await HandleButtonAsync(component);
                    };

                    await client.LoginAsync(TokenType.Bot, config.BotToken);
                    await client.StartAsync();

                    _clientsByToken[config.BotToken] = client;
                    _tokenRefCounts[config.BotToken] = 0;
                }

                _tokenRefCounts[config.BotToken]++;

                // Index allowed channel IDs for quick lookup
                if (config.AllowedChannelIds is not null)
                {
                    foreach (var discordChannelId in config.AllowedChannelIds)
                    {
                        _channelsByDiscordChannelId[discordChannelId] = channel;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Discord adapter for channel '{Name}'", channel.Name);
            }
        }

        _logger.LogInformation("Discord adapter started with {Count} channel(s)", channelsList.Count);
    }

    public async Task StopAsync(Channel channel)
    {
        var config = DiscordConfig.FromJson(channel.Config);
        if (string.IsNullOrWhiteSpace(config.BotToken))
            return;

        if (_tokenRefCounts.TryGetValue(config.BotToken, out var count))
        {
            count--;
            if (count <= 0)
            {
                _tokenRefCounts.Remove(config.BotToken);
                if (_clientsByToken.TryGetValue(config.BotToken, out var client))
                {
                    await client.StopAsync();
                    client.Dispose();
                    _clientsByToken.Remove(config.BotToken);
                }
            }
            else
            {
                _tokenRefCounts[config.BotToken] = count;
            }
        }

        // Clean up channel index
        if (config.AllowedChannelIds is not null)
        {
            foreach (var id in config.AllowedChannelIds)
                _channelsByDiscordChannelId.Remove(id);
        }
    }

    public async Task SendMessageAsync(Channel channel, string identityId, string text)
    {
        var config = DiscordConfig.FromJson(channel.Config);
        if (string.IsNullOrWhiteSpace(config.BotToken))
            return;

        if (!_clientsByToken.TryGetValue(config.BotToken, out var client))
            return;

        // identityId is the Discord channel ID where the original message came from
        if (ulong.TryParse(identityId, out var discordChannelId))
        {
            if (await client.GetChannelAsync(discordChannelId) is IMessageChannel msgChannel)
            {
                // Discord has a 2000 char limit; split if needed
                if (text.Length <= 2000)
                {
                    await msgChannel.SendMessageAsync(text);
                }
                else
                {
                    // Split into chunks of ~1900 chars to be safe
                    for (var i = 0; i < text.Length; i += 1900)
                    {
                        var chunk = text.Substring(i, Math.Min(1900, text.Length - i));
                        await msgChannel.SendMessageAsync(chunk);
                    }
                }
            }
        }
    }

    public async Task<bool> SendApprovalAsync(Channel channel, string identityId, WorkspaceApprovalEvent approval)
    {
        var config = DiscordConfig.FromJson(channel.Config);
        if (string.IsNullOrWhiteSpace(config.BotToken))
            return false;

        if (!_clientsByToken.TryGetValue(config.BotToken, out var client))
            return false;

        if (!ulong.TryParse(identityId, out var discordChannelId))
            return false;

        if (await client.GetChannelAsync(discordChannelId) is not IMessageChannel msgChannel)
            return false;

        var embed = new EmbedBuilder()
            .WithTitle("Approval Required")
            .WithDescription(approval.Description ?? approval.ActionType.ToString())
            .WithColor(Color.Orange)
            .AddField("Action", approval.ActionType.ToString(), true)
            .AddField("Risk", approval.RiskLevel.ToString(), true);

        if (!string.IsNullOrWhiteSpace(approval.TargetPath))
            embed.AddField("Target", approval.TargetPath, false);

        if (!string.IsNullOrWhiteSpace(approval.CommandPreview))
            embed.AddField("Command", $"```{approval.CommandPreview}```", false);

        var builder = new ComponentBuilder()
            .WithButton("Approve", $"approval:approve:{approval.ApprovalToken}", ButtonStyle.Success)
            .WithButton("Reject", $"approval:reject:{approval.ApprovalToken}", ButtonStyle.Danger);

        await msgChannel.SendMessageAsync(embed: embed.Build(), components: builder.Build());
        return true;
    }

    private async Task HandleMessageAsync(SocketMessage message, Func<Channel, string, string, Task> onMessage)
    {
        // Ignore bot/webhook messages
        if (message.Author.IsBot || message.Author.IsWebhook)
            return;

        // Only handle user messages in guild text channels and DMs
        if (message.Channel is not SocketTextChannel and not SocketDMChannel)
            return;

        var discordChannelId = message.Channel.Id;
        var authorId = message.Author.Id;

        // Find the channel config that matches this Discord channel
        Channel? channel = null;

        // First check direct channel ID mapping
        if (_channelsByDiscordChannelId.TryGetValue(discordChannelId, out var mappedChannel))
        {
            channel = mappedChannel;
        }
        else
        {
            // Check all channels to see if this message falls within their allowed scope
            foreach (var (_, client) in _clientsByToken)
            {
                // Find channels that allow this guild/channel/user
                foreach (var (_, ch) in _channelsByDiscordChannelId)
                {
                    var config = DiscordConfig.FromJson(ch.Config);

                    // Check guild
                    if (message.Channel is SocketTextChannel guildChannel)
                    {
                        if (config.AllowedGuildIds is not null
                            && !config.AllowedGuildIds.Contains(guildChannel.Guild.Id))
                            continue;
                    }

                    // Check channel
                    if (config.AllowedChannelIds is not null
                        && !config.AllowedChannelIds.Contains(discordChannelId))
                        continue;

                    // Check user
                    if (config.AllowedUserIds is not null
                        && !config.AllowedUserIds.Contains(authorId))
                        continue;

                    // DM check
                    if (message.Channel is SocketDMChannel && !config.DmEnabled)
                        continue;

                    channel = ch;
                    break;
                }

                if (channel is not null)
                    break;
            }
        }

        if (channel is null)
            return;

        // Use the Discord channel ID as the identity so responses go back to the same channel
        var identityId = discordChannelId.ToString();

        try
        {
            await onMessage(channel, identityId, message.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Discord message from user {UserId} in channel {ChannelId}",
                authorId, discordChannelId);
        }
    }

    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;

        if (!customId.StartsWith("approval:"))
            return;

        var parts = customId.Split(':', 3);
        if (parts.Length != 3)
            return;

        var action = parts[1]; // "approve" or "reject"
        var token = parts[2];

        // Acknowledge the interaction
        await component.DeferAsync();

        // The actual approval resolution happens via the API endpoint.
        // We just acknowledge the button click and let the user know.
        // The web UI or another mechanism handles the actual resolution.
        // For now, we'll update the message to show it was acknowledged.

        var actionText = action == "approve" ? "approved" : "rejected";
        await component.ModifyOriginalResponseAsync(props =>
        {
            props.Content = $"Approval {actionText} — processing...";
            props.Components = new ComponentBuilder().Build();
            props.Embed = null;
        });

        // TODO: In the future, the adapter could directly call the approval API.
        // For now, the user resolves approvals via the web UI.
    }
}
