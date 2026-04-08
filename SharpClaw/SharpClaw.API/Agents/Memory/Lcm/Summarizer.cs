using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SharpClaw.API.Helpers;

namespace SharpClaw.API.Agents.Memory.Lcm;

public partial class Summarizer(ChatProvider chatProvider)
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const string LcmSummaryLevelKey = "lcm_summary_level";
    private const string LcmSummaryIdKey = "lcm_summary_id";

    public async Task<ChatResponse> Summarize(AgentExecutionContext context,
        List<ChatResponse> previousHistory, List<ChatResponse> messages, int depth, bool aggressive = false)
    {
        var priorContent = FormatSummaries(previousHistory);

        var summaryContent = depth == 0
            ? string.Join('\n', FormatMessagesForSummary(messages.SelectMany(m => m.Messages)))
            : FormatSummaries(messages);

        var summaryMessage = SummaryMessage(summaryContent, priorContent);

        var prompt = depth switch
        {
            0 => SummaryPrompt,
            1 => CondenseD2Prompt,
            _ => CondenseD3Prompt,
        };

        if (aggressive)
            prompt = $"{prompt.Trim()}\n\n{PromptAggressiveDirective}";

        // TODO: set up client without tools / other useless stuff
        var result = await chatProvider
            .GetClient(context)
            .GetResponse(
                [
                    new ChatMessage(ChatRole.System, prompt),
                    new ChatMessage(ChatRole.User, summaryMessage),
                ], []
            );

        var msgTxt = result.Responses.FirstOrDefault(m => !string.IsNullOrEmpty(m.Text));

        if (string.IsNullOrEmpty(msgTxt?.Text))
            throw new Exception("No summary message found");

        msgTxt.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        msgTxt.AdditionalProperties[LcmSummaryLevelKey] = depth + 1;
        msgTxt.AdditionalProperties[LcmSummaryIdKey] = GenerateSummaryId(msgTxt.Text, DateTime.UtcNow);

        return msgTxt;
    }

    public static (
        List<ChatResponse> PreSummary,
        List<ChatResponse> ToSummarize,
        List<ChatResponse> PostSummary,
        int Depth
        ) SplitMessages(List<ChatResponse> messages, int mostRecentMessagesToKeep)
    {
        var messagesWithLevel = messages
            .Select((m, i) => (
                Message: m,
                Depth: m.AdditionalProperties?.TryGetValue(LcmSummaryLevelKey, out var level) ?? false
                    ? level as int? ?? 0
                    : 0))
            .ToArray();

        var messagesWithId = messages.Select((m, i) => (Message: m, Id: i)).ToArray();

        foreach (var level in messagesWithLevel.Select(m => m.Depth).Distinct().OrderBy(l => l))
        {
            var tail = messagesWithLevel
                .Where(m => m.Depth == 0)
                .TakeLast(mostRecentMessagesToKeep)
                .ToList();

            var summary = messagesWithLevel
                .Where(m => m.Depth == level && !tail.Contains(m))
                .ToList();

            var summaryToolCalls = summary
                .SelectMany(r => r.Message.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()))
                .Select(c => c.CallId)
                .ToArray();

            var messagesToShift = tail
                .Where(r => r.Message
                    .Messages
                    .SelectMany(m => m.Contents)
                    .Any(c => c is FunctionResultContent result && summaryToolCalls.Contains(result.CallId)))
                .ToArray();

            summary.AddRange(messagesToShift);
            tail.RemoveAll(m => messagesToShift.Contains(m));

            var preSummary = messagesWithLevel
                .Where(m => !summary.Contains(m) && !tail.Contains(m))
                .ToList();

            Trace.Assert(preSummary.Count + summary.Count + tail.Count == messages.Count, "All messages should be accounted for");

            if (summary.Count > 1)
                return (
                    preSummary.Select(m => m.Message).ToList(),
                    summary.Select(m => m.Message).ToList(),
                    tail.Select(m => m.Message).ToList(),
                    level
                );
        }

        return ([], [], [], -1); // failure case, what to do here?
    }

    private static string GenerateSummaryId(string content, DateTime timestamp)
    {
        var sha = SHA256.Create();
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var timestampBytes = BitConverter.GetBytes(timestamp.Ticks);
        var combinedBytes = contentBytes.Concat(timestampBytes).ToArray();
        var hashBytes = sha.ComputeHash(combinedBytes);
        var encoder = new RadixEncoding(Alphabet);
        return $"sum_{encoder.Encode(hashBytes)[..16]}";
    }

    private static string FormatSummaries(List<ChatResponse> messages)
    {
        Trace.Assert(messages.All(m =>
            m.Messages.Count == 1
            && (m.AdditionalProperties?.ContainsKey(LcmSummaryIdKey) ?? false)
        ), "All messages should be summaries");

        return FormatMessagesForCondense(messages
            .SelectMany(r => r.Messages.Select(m => (
                m.AdditionalProperties?[LcmSummaryIdKey]?.ToString() ?? "",
                m.Text
            ))).ToList());
    }
}