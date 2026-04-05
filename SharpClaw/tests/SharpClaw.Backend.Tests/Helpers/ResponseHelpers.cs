using System.Text.Json;

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

    public static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}