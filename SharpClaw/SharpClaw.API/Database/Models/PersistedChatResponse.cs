using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SharpClaw.API.Database.Models;

internal sealed class PersistedChatResponse
{
    public Dictionary<string, object?>? AdditionalProperties { get; init; }
    public List<PersistedChatMessage> Messages { get; init; } = [];

    public static PersistedChatResponse From(ChatResponse response)
    {
        return new PersistedChatResponse
        {
            AdditionalProperties = ToPrimitiveDictionary(response.AdditionalProperties),
            Messages = response.Messages.Select(PersistedChatMessage.From).ToList(),
        };
    }

    public ChatResponse ToChatResponse()
    {
        var messages = Messages.Select(m => m.ToChatMessage()).ToList();
        var response = new ChatResponse(messages);

        if (AdditionalProperties is not null)
            response.AdditionalProperties = ToAdditionalProperties(AdditionalProperties);

        return response;
    }

    private static Dictionary<string, object?>? ToPrimitiveDictionary(IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null || source.Count == 0)
            return null;

        return source.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static AdditionalPropertiesDictionary ToAdditionalProperties(Dictionary<string, object?> source)
    {
        var properties = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in source)
            properties[key] = ConvertJsonLikeValue(value);

        return properties;
    }

    private static object? ConvertJsonLikeValue(object? value)
    {
        if (value is JsonElement element)
            return ConvertJsonElement(element);

        return value;
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