using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace SharpClaw.API.Agents.Memory.Lcm;

public partial class Summarizer(ChatProvider chatProvider)
{
    private static JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<List<ChatMessage>> Summarize(AgentExecutionContext context,
        List<ChatMessage> previousHistory, List<ChatMessage> messages)
    {
        Console.WriteLine("###Summarizing:");
        Console.WriteLine($"#####Previous history:\n{JsonSerializer.Serialize(previousHistory, _jsonOptions)}");
        Console.WriteLine($"#####New messages\n{JsonSerializer.Serialize(messages, _jsonOptions)}");

        var summaryMessage = SummaryMessage(FormatMessagesForSummary(messages), null);

        Console.WriteLine($"#####Summary message:\n{summaryMessage}");

        var result = await chatProvider
            .GetClient(context)
            .GetResponse(
                [
                    new ChatMessage(ChatRole.System, SummaryPrompt),
                    new ChatMessage(ChatRole.User, summaryMessage),
                ], []
            );

        Console.WriteLine($"#####Result:\n{JsonSerializer.Serialize(result, _jsonOptions)}");

        return result;
    }

    private static IEnumerable<string> FormatMessagesForSummary(List<ChatMessage> messages)
    {
        string? lastMessageId = null;
        var parts = messages
            .SelectMany(m => m.Contents.Select(p => (Message: m, Part: p)))
            .ToArray();

        foreach (var p in parts)
        {
            if (lastMessageId != p.Message.MessageId)
            {
                if (lastMessageId is not null)
                    yield return string.Empty;

                yield return $"[Message {p.Message.MessageId}] ({p.Message.Role.ToString().ToUpper()})";
                lastMessageId = p.Message.MessageId;
            }

            if (p.Part is TextContent textContent)
            {
                yield return textContent.Text;
            }

            if (p.Part is FunctionCallContent functionCall)
            {
                yield return $"[Tool {functionCall.Name}]";
                yield return $"Input: {JsonSerializer.Serialize(functionCall.Arguments)}";

                var result = parts.FirstOrDefault(r =>
                    r.Part is FunctionResultContent resultCall
                    && resultCall.CallId == functionCall.CallId);

                if (result.Part is not null)
                {
                    var functionResult = ((FunctionResultContent)result.Part).Result;
                    var formattedResult = functionResult as string ?? JsonSerializer.Serialize(functionResult);
                    yield return $"Output: {formattedResult}";
                }
            }
        }
    }
}