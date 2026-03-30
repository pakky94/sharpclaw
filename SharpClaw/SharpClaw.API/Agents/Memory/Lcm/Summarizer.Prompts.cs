using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SharpClaw.API.Agents.Memory.Lcm;

public partial class Summarizer
{
    private const string SummaryPrompt =
        """
        # Upward d1 Message Summarization Prompt

        You are summarizing a chronological slice of a coding conversation.

        ## Goal

        Produce one narrative summary that lets a future model continue work without reading the full underlying messages.

        Write the summary as a coherent story of the turns in order:
        - How this segment started (initial ask, context, constraints)
        - What happened while working (key decisions, attempts, discoveries, changes)
        - How this segment ended (resolved items, abandoned paths, and in-progress work)

        ## Required Content

        Include:
        - User intent and constraints that shaped implementation
        - Key technical decisions and why they mattered
        - Concrete code changes (files, functions, components, APIs)
        - Important tool findings (errors, test results, search findings) that changed direction
        - Current end-state: done, deferred, blocked, or still in progress

        ## Style

        - Keep chronological flow explicit
        - Prefer concrete details over abstract phrasing
        - Keep prose compact and information-dense
        - Use clear section headings and bullets where helpful

        ## Output Guidance

        - Target length: 1800-2000 tokens
        - Do not add synthetic metadata headers, frontmatter, IDs, token estimates, or placeholder fields
        - Focus on durable context needed for continuation
        """;

    private const string CondenseD2Prompt =
        """
        # Upward d2 Summary Condensation Prompt

        You are condensing multiple chronological summaries into one higher-level narrative summary.

        ## Goal

        Merge the input summaries into a single summary that preserves continuity and can be used to keep working without re-reading all source summaries.

        Treat the input as a time-ordered sequence and produce a unified story:
        - How the overall effort began
        - How the work evolved across the grouped summaries
        - Where the effort stands at the end

        ## Required Content

        Include:
        - Major decisions and how they shifted the implementation path
        - Important code changes and touched areas of the codebase
        - Key discoveries from tools/tests/errors that changed outcomes
        - Explicit transitions: what was pursued, what was dropped, what replaced it
        - Final state: completed work, open threads, and next likely actions

        ## Style

        - Preserve chronology across the merged summaries
        - Resolve repetition by merging overlapping points cleanly
        - Keep language precise and implementation-oriented
        - Use structured headings and concise bullets where useful

        ## Output Guidance

        - Target length: 1800-2000 tokens
        - Do not add synthetic metadata headers, frontmatter, IDs, token estimates, or placeholder fields
        - Prioritize context that enables accurate continuation of work
        """;

    private const string CondenseD3Prompt =
        """
        # Upward d3+ Summary Condensation Prompt

        You are condensing higher-order chronological summaries into one next-order narrative summary.

        ## Goal

        Merge the input summaries into a single summary that preserves continuity and enables reliable continuation without replaying all parent summaries.

        Treat the inputs as a time-ordered sequence and produce one coherent narrative:
        - How the larger effort progressed across these already-condensed layers
        - What changed in strategy, implementation, or scope over time
        - Where the effort stands now

        ## Required Content

        Include:
        - Major decisions and their downstream effects
        - Important code changes and touched components
        - Material discoveries from tests/errors/tools that redirected work
        - Explicit transitions between attempts, including abandoned approaches
        - Current terminal state: completed work, unresolved threads, and likely next actions

        ## Style

        - Preserve chronology and causality
        - Compress repetition while keeping decisive detail
        - Keep language concrete, implementation-oriented, and information-dense
        - Use headings and concise bullets where useful

        ## Output Guidance

        - Target length: 1800-2000 tokens
        - Do not add synthetic metadata headers, frontmatter, IDs, token estimates, or placeholder fields
        - Focus on durable context needed to continue accurately
        """;

    private const string PromptAggressiveDirective =
        """
        ## Aggressive Compression Override
        - You are in escalation pass 2 because pass 1 was not shorter than input.
        - Compress more aggressively than normal while preserving task-critical facts.
        - Remove repetition, low-value narrative, and secondary detail.
        - Output must still be coherent and safe for continuation.
        """;

    private static string SummaryMessage(string formattedMessages, string? priorContext)
        => string.IsNullOrWhiteSpace(priorContext)
            ? $"""
               The preceding summaries in this chain are as follows:

               <preceding_summaries>
               {priorContext}
               </preceding_summaries>

               The new segment is:

               <messages>
               {formattedMessages}
               </messages>

               Summarize only the new segment while maintaining narrative continuity with the preceding summaries.
               Do not continue or answer the source conversation directly.
               """
            : $"""
               The following content is source material to summarize according to the system instructions above.

               <messages>
               {formattedMessages}
               </messages>

               Produce a chronological narrative summary of this source material.
               Do not continue or answer the source conversation directly.
               """;

    private static IEnumerable<string> FormatMessagesForSummary(IEnumerable<ChatMessage> messages)
    {
        string? lastMessageId = null;
        var parts = messages
            .SelectMany(m => m.Contents.Select(p => (Message: m, Part: p)))
            .ToArray();

        foreach (var p in parts)
        {
            if (p.Part is FunctionResultContent)
                continue;

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
                    var formattedResult = functionResult switch
                    {
                        string s => s,
                        JsonElement { ValueKind: JsonValueKind.String } t => t.GetString(),
                        _ => JsonSerializer.Serialize(functionResult)
                    };
                    yield return $"Output: {formattedResult}";
                }
            }
        }
    }

    private static string FormatMessagesForCondense(List<(string SummaryId, string Content)> messages)
        => $"""
            ## Input Summary IDs

            {string.Join(", ", messages.Select(m => m.SummaryId))}

            ## Summaries to Condense

            {string.Join("\n", messages.Select(m =>
                $"""
                 --- Summary {m.SummaryId} ---
                 {m.Content}

                 """))}
            """.TrimEnd();
}