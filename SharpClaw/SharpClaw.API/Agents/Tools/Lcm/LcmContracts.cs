namespace SharpClaw.API.Agents.Tools.Lcm;

public static class LcmId
{
    public static bool IsSummary(string id) => id.StartsWith("sum_", StringComparison.OrdinalIgnoreCase);
    public static bool IsFile(string id) => id.StartsWith("file_", StringComparison.OrdinalIgnoreCase);
}

public sealed record LcmDescribeRequest(string Id);
public sealed record LcmExpandRequest(string SummaryId);
public sealed record LcmReadRequest(string FileId, int? MaxBytes = null);
public sealed record LcmGrepRequest(string Pattern, Guid SessionId, string? SummaryId = null, int Page = 1);
public sealed record LcmExpandQueryRequest(
    string Prompt,
    Guid SessionId,
    IReadOnlyList<string>? SummaryIds = null,
    string? Query = null,
    int? MaxTokens = null);

public sealed record LcmDescribeMetadata(
    string Id,
    string Type,
    bool Found,
    string? SummaryKind = null,
    int? SummaryLevel = null,
    bool? OffContext = null,
    int? ParentSummaryCount = null);

public sealed record LcmExpandMetadata(
    string SummaryId,
    bool Found,
    int MessageCount,
    int SummaryLevel,
    int ParentSummaryCount);

public sealed record LcmGrepMetadata(
    string Pattern,
    Guid SessionId,
    string? SummaryId,
    int Page,
    int MatchCount,
    bool HasMore);
