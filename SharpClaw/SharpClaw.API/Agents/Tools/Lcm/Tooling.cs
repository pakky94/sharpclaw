using Microsoft.Extensions.AI;
using SharpClaw.API.Database;

namespace SharpClaw.API.Agents.Tools.Lcm;

public static class LcmTools
{
    private const int GrepPageSize = 50;
    private const int GrepMaxBytesPerPage = 40_000;

    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(
            Describe,
            "lcm_describe",
            "Look up metadata and summary information for an LCM ID (file_xxx or sum_xxx)."),
        AIFunctionFactory.Create(
            Expand,
            "lcm_expand",
            "Expand an LCM summary (sum_xxx) to its underlying messages."),
        AIFunctionFactory.Create(
            Grep,
            "lcm_grep",
            "Search prior conversation messages with a regex pattern."),
    ];

    public static async Task<object> Describe(IServiceProvider serviceProvider, string id)
    {
        var trimmedId = (id ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedId))
        {
            return new
            {
                title = "LCM describe",
                metadata = new LcmDescribeMetadata("", "unknown", false),
                output = "Missing id. Expected file_xxx or sum_xxx.",
            };
        }

        if (LcmId.IsSummary(trimmedId))
            return await DescribeSummary(serviceProvider, trimmedId);

        if (LcmId.IsFile(trimmedId))
            return DescribeFilePhase1(trimmedId);

        return new
        {
            title = $"LCM describe: {trimmedId}",
            metadata = new LcmDescribeMetadata(trimmedId, "unknown", false),
            output = $"Unknown LCM ID format: \"{trimmedId}\". Expected file_xxx or sum_xxx.",
        };
    }

    private static object DescribeFilePhase1(string fileId)
    {
        return new
        {
            title = $"LCM file: {fileId}",
            metadata = new LcmDescribeMetadata(fileId, "file", false),
            output =
                """
                File lookup is not available yet in this SharpClaw phase.

                Current LCM implementation supports summary metadata first. File-backed LCM storage/retrieval
                will be added in the next phase with dedicated tables and repository queries.
                """.Trim(),
        };
    }

    private static async Task<object> DescribeSummary(IServiceProvider serviceProvider, string summaryId)
    {
        var context = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var repository = new Repository(configuration);

        var summary = await repository.GetLcmSummary(context.SessionId, summaryId);
        if (summary is null)
        {
            return new
            {
                title = $"LCM summary: {summaryId}",
                metadata = new LcmDescribeMetadata(summaryId, "summary", false),
                output = $"Summary not found: {summaryId}",
            };
        }

        var parents = await repository.GetLcmSummaryParents(summary.DbId);
        var parentSummaryIds = parents
            .Select(p => p.LcmSummaryId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();

        var lines = new List<string>
        {
            $"## LCM Summary: {summaryId}",
            "",
            $"**Session:** {summary.SessionId}",
            $"**Summary Level:** {(summary.SummaryLevel?.ToString() ?? "unknown")}",
            $"**Created:** {summary.CreatedAt:O}",
            $"**Parent Summary Count:** {parents.Count}",
            $"**Parent Summary IDs:** {(parentSummaryIds.Length > 0 ? string.Join(", ", parentSummaryIds) : "-")}",
            "",
            "## Summary Content",
            "",
            string.IsNullOrWhiteSpace(summary.Content) ? "(empty summary content)" : summary.Content,
        };

        return new
        {
            title = $"LCM summary: {summaryId}",
            metadata = new LcmDescribeMetadata(
                Id: summaryId,
                Type: "summary",
                Found: true,
                SummaryKind: "upward",
                SummaryLevel: summary.SummaryLevel,
                OffContext: false,
                ParentSummaryCount: parents.Count),
            output = string.Join("\n", lines),
        };
    }

    public static async Task<object> Expand(IServiceProvider serviceProvider, string summary_id)
    {
        var summaryId = (summary_id ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(summaryId))
        {
            return new
            {
                title = "Expand summary",
                metadata = new LcmExpandMetadata("", false, 0, 0, 0),
                output = "Missing summary_id. Expected sum_xxx.",
            };
        }

        if (LcmId.IsFile(summaryId))
        {
            return new
            {
                title = $"Cannot expand file: {summaryId}",
                metadata = new LcmExpandMetadata(summaryId, false, 0, 0, 0),
                output =
                    $"ERROR: lcm_expand only works with summary IDs (sum_xxx). \"{summaryId}\" looks like a file ID.\nUse lcm_describe on {summaryId} for metadata.",
            };
        }

        if (!LcmId.IsSummary(summaryId))
        {
            return new
            {
                title = $"Expand summary: {summaryId}",
                metadata = new LcmExpandMetadata(summaryId, false, 0, 0, 0),
                output = $"Invalid summary ID format: \"{summaryId}\". Expected sum_xxx.",
            };
        }

        var context = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var repository = new Repository(configuration);

        var summary = await repository.GetLcmSummary(context.SessionId, summaryId);
        if (summary is null)
        {
            return new
            {
                title = $"Expand summary: {summaryId}",
                metadata = new LcmExpandMetadata(summaryId, false, 0, 0, 0),
                output = $"Summary not found: {summaryId}",
            };
        }

        var parents = await repository.GetLcmSummaryParents(summary.DbId);
        var parentSummaryIds = parents
            .Select(p => p.LcmSummaryId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
        var messages = await repository.ExpandLcmSummaryToMessages(context.SessionId, summaryId);

        var metadataLines = new[]
        {
            "Summary metadata:",
            $"- session_id: {summary.SessionId}",
            $"- level: {(summary.SummaryLevel?.ToString() ?? "unknown")}",
            $"- parent_summary_count: {parents.Count}",
            $"- parent_summary_ids: {(parentSummaryIds.Length > 0 ? string.Join(", ", parentSummaryIds) : "-")}",
        };

        if (messages.Count == 0)
        {
            return new
            {
                title = $"Expanded: {summaryId} (0 messages)",
                metadata = new LcmExpandMetadata(
                    SummaryId: summaryId,
                    Found: true,
                    MessageCount: 0,
                    SummaryLevel: summary.SummaryLevel ?? 0,
                    ParentSummaryCount: parents.Count),
                output = $"{string.Join("\n", metadataLines)}\n\nSummary found but no underlying messages were linked.\n\nSummary content:\n{summary.Content}",
            };
        }

        var expanded = messages
            .Select((m, idx) => $"--- Message {idx + 1} (db_id: {m.MessageDbId}, role: {m.Role}) ---\n{m.Content}")
            .ToArray();

        return new
        {
            title = $"Expanded: {summaryId} ({messages.Count} messages)",
            metadata = new LcmExpandMetadata(
                SummaryId: summaryId,
                Found: true,
                MessageCount: messages.Count,
                SummaryLevel: summary.SummaryLevel ?? 0,
                ParentSummaryCount: parents.Count),
            output = $"{string.Join("\n", metadataLines)}\n\n{string.Join("\n\n", expanded)}",
        };
    }

    public static async Task<object> Grep(
        IServiceProvider serviceProvider,
        string pattern,
        string? summary_id = null,
        int page = 1)
    {
        var normalizedPattern = (pattern ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPattern))
        {
            return new
            {
                title = "LCM grep",
                metadata = new LcmGrepMetadata("", Guid.Empty, summary_id, page, 0, false),
                output = "Missing pattern. Provide a valid regex pattern.",
            };
        }

        if (page < 1)
            page = 1;

        var context = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var repository = new Repository(configuration);

        var offset = (page - 1) * GrepPageSize;
        var fetched = await repository.SearchLcmMessagesRegex(
            context.SessionId,
            normalizedPattern,
            summary_id,
            GrepPageSize + 1,
            offset);

        var hasMore = fetched.Count > GrepPageSize;
        var matches = fetched.Take(GrepPageSize).ToArray();
        var grouped = matches
            .GroupBy(x => x.CoveringSummaryId ?? "(no summary)")
            .ToArray();

        var outputLines = new List<string>
        {
            "## Regex Search Results",
            $"Pattern: `{normalizedPattern}`",
            $"Session ID: {context.SessionId}",
            $"Page: {page}",
        };
        if (!string.IsNullOrWhiteSpace(summary_id))
            outputLines.Add($"Scoped to summary: {summary_id}");
        outputLines.Add(string.Empty);

        var currentBytes = string.Join("\n", outputLines).Length;
        var displayedCount = 0;
        foreach (var group in grouped)
        {
            var representative = group.First();
            var summaryInfo = representative.CoveringSummaryId is null
                ? string.Empty
                : $" [level={representative.CoveringSummaryLevel?.ToString() ?? "unknown"}]";
            var header = $"### Covered by: {group.Key}{summaryInfo}\n";
            if (currentBytes + header.Length > GrepMaxBytesPerPage)
                break;

            outputLines.Add(header);
            currentBytes += header.Length;

            foreach (var match in group)
            {
                var snippet = TruncateSingleLine(match.Content, 220);
                var line = $"- [db_id={match.MessageDbId}] ({match.Role}) {snippet}\n";
                if (currentBytes + line.Length > GrepMaxBytesPerPage)
                    break;

                outputLines.Add(line);
                currentBytes += line.Length;
                displayedCount++;
            }

            outputLines.Add(string.Empty);
            currentBytes++;
        }

        if (matches.Length == 0)
        {
            outputLines.Add("No matches found for the given pattern.");
        }
        else if (hasMore || displayedCount < matches.Length)
        {
            outputLines.Add($"---\nMore results available. Use page={page + 1} to see more.");
        }

        return new
        {
            title = $"LCM grep: {normalizedPattern}",
            metadata = new LcmGrepMetadata(
                Pattern: normalizedPattern,
                SessionId: context.SessionId,
                SummaryId: summary_id,
                Page: page,
                MatchCount: displayedCount,
                HasMore: hasMore || displayedCount < matches.Length),
            output = string.Join("\n", outputLines),
        };
    }

    private static string TruncateSingleLine(string value, int maxLength)
    {
        var singleLine = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Length <= maxLength)
            return singleLine;

        return singleLine[..(maxLength - 3)] + "...";
    }
}
