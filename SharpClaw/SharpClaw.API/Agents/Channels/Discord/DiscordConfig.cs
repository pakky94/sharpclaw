using System.Text.Json;

namespace SharpClaw.API.Agents.Channels.Discord;

public class DiscordConfig
{
    public string BotToken { get; set; } = string.Empty;
    public List<ulong>? AllowedGuildIds { get; set; }
    public List<ulong>? AllowedChannelIds { get; set; }
    public List<ulong>? AllowedUserIds { get; set; }
    public bool DmEnabled { get; set; } = true;

    public static DiscordConfig FromJson(string json)
    {
        return JsonSerializer.Deserialize<DiscordConfig>(json)
               ?? new DiscordConfig();
    }
}
