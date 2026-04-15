using System.Text.Json;
using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests.Helpers;

public static class ResponseHelpers
{
    public static List<JsonElement> FlattenMessageContents(JsonDocument history)
    {
        var result = new List<JsonElement>();
        foreach (var message in history.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (!message.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
                continue;

            result.AddRange(contents.EnumerateArray());
        }

        return result;
    }

    public static IEnumerable<string> GetMessageTexts(JsonDocument history)
    {
        foreach (var message in history.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (message.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                yield return textElement.GetString() ?? string.Empty;
        }
    }

    public static int IndexOf(IReadOnlyList<StreamEvent> items, string type)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Type, type, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}