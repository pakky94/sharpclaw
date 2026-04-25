using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SharpClaw.API.Database.Models;

internal sealed class PersistedContent
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? CallId { get; init; }
    public string? ToolName { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
    public string? Payload { get; init; }

    public static PersistedContent From(AIContent content)
    {
        return content switch
        {
            TextContent textContent => new PersistedContent
            {
                Type = "text",
                Text = textContent.Text,
            },
            FunctionCallContent functionCall => new PersistedContent
            {
                Type = "tool_call",
                CallId = functionCall.CallId,
                ToolName = functionCall.Name,
                Arguments = Serialize(functionCall.Arguments),
            },
            FunctionResultContent functionResult => new PersistedContent
            {
                Type = "tool_result",
                CallId = functionResult.CallId,
                Result = Serialize(functionResult.Result),
            },
            TextReasoningContent reasoningContent => new PersistedContent
            {
                Type = "reasoning",
                Text = reasoningContent.Text,
            },
            _ => new PersistedContent
            {
                Type = "unknown",
                Payload = Serialize(content),
            },
        };
    }

    public string ToFallbackText()
    {
        return Type switch
        {
            "tool_call" => $"[tool_call] {ToolName}({Arguments})",
            "tool_result" => $"[tool_result] {CallId}: {Result}",
            "reasoning" => string.Empty,
            "unknown" => $"[content] {Payload}",
            _ => Text ?? string.Empty,
        };
    }

    public AIContent? ToAiContent()
    {
        return Type switch
        {
            "text" when !string.IsNullOrWhiteSpace(Text) => new TextContent(Text),
            "tool_call" when !string.IsNullOrWhiteSpace(CallId) && !string.IsNullOrWhiteSpace(ToolName)
                => new FunctionCallContent(CallId, ToolName, DeserializeArguments(Arguments)),
            "tool_result" when !string.IsNullOrWhiteSpace(CallId)
                => new FunctionResultContent(CallId, DeserializeJsonLikeValue(Result)),
            "reasoning" when !string.IsNullOrWhiteSpace(Text)
                => new TextReasoningContent(Text),
            _ => null,
        };
    }

    private static string? Serialize(object? value)
    {
        if (value is null)
            return null;

        if (value is string text)
            return text;

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString();
        }
    }

    private static object? DeserializeJsonLikeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            using var json = JsonDocument.Parse(value);
            return ConvertJsonElement(json.RootElement);
        }
        catch
        {
            return value;
        }
    }

    private static IDictionary<string, object?>? DeserializeArguments(string? value)
    {
        var parsed = DeserializeJsonLikeValue(value);
        return parsed as IDictionary<string, object?>;
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