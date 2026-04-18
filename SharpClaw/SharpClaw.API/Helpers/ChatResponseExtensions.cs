using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SharpClaw.API.Helpers;

public static class ChatResponseExtensions
{
    public static long EstimatedTokenCount(this List<ChatResponse> messages)
    {
        var lastActualUsageMessage = messages
            .LastOrDefault(m => m.Usage?.InputTokenCount is not null);

        var actualTokenCount = lastActualUsageMessage?.Usage?.InputTokenCount ?? 0;
        var idx = lastActualUsageMessage is null ? -1 :messages.IndexOf(lastActualUsageMessage);

        actualTokenCount += messages
            .Skip(idx + 1)
            .SelectMany(r => r.Messages)
            .SelectMany(m => m.Contents)
            .Sum(c => c.EstimatedTokenCount());

        return actualTokenCount;
    }

    private static long EstimatedTokenCount(this AIContent content)
        => content switch
        {
            TextContent t => Convert.ToInt64(t.Text.Length * 0.25),
            FunctionResultContent fr => Convert.ToInt64(.3 * (
                50 +
                JsonSerializer.Serialize(fr.Result).Length
            )),
            FunctionCallContent fc => Convert.ToInt64(0.3 * (
                50 +
                fc.Name.Length +
                JsonSerializer.Serialize(fc.Arguments).Length
            )),
            _ => 0,
        };
}