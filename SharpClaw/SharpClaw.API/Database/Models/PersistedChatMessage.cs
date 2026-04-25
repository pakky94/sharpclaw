using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SharpClaw.API.Database.Models;

internal sealed class PersistedChatMessage
{
    public required string Role { get; init; }
    public string? Text { get; init; }
    public string? AuthorName { get; init; }
    public string? MessageId { get; init; }
    public Dictionary<string, object?>? AdditionalProperties { get; init; }
    public List<PersistedContent> Contents { get; init; } = [];

    public static PersistedChatMessage From(ChatMessage message)
    {
        return new PersistedChatMessage
        {
            Role = message.Role.Value,
            Text = message.Text,
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Contents = message.Contents.Select(PersistedContent.From).ToList(),
        };
    }

    public ChatMessage ToChatMessage()
    {
        var message = new ChatMessage(new ChatRole(Role), Text)
        {
            AuthorName = AuthorName,
            MessageId = MessageId,
        };

        if (AdditionalProperties is not null && AdditionalProperties.Count > 0)
        {
            var props = new AdditionalPropertiesDictionary();
            foreach (var (key, value) in AdditionalProperties)
                props[key] = value is JsonElement element ? ConvertJsonElement(element) : value;

            message.AdditionalProperties = props;
        }

        var skipTextContents = !string.IsNullOrWhiteSpace(Text);
        foreach (var content in Contents)
        {
            var aiContent = content.ToAiContent();
            if (aiContent is null)
                continue;

            if (skipTextContents && aiContent is TextContent)
                continue;

            message.Contents.Add(aiContent);
        }

        return message;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var i) => i,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.ToString(),
        };
    }
}