using System.Text;

namespace SharpClaw.API.Helpers;

public static class StringReplacer
{
    private delegate (string? NewFile, Error? Error) Replacer(string content, string oldString, string newString, bool replaceAll);

    public static (string? NewFile, Error? Error) Replace(string content,
        string oldString, string newString, bool replaceAll)
    {
        foreach (var replacer in (IEnumerable<Replacer>)[
                     ReplaceExactMatch,
                     ReplaceLineMatches,
                 ])
        {
            var result = replacer(content, oldString, newString, replaceAll);

            if (result.Error == Error.MultipleMatchesFound)
                return (null, Error.MultipleMatchesFound);

            if (result.Error is null)
                return result;
        }

        return (null, Error.OldStringNotFound);
    }

    private static (string? NewFile, Error? Error) ReplaceExactMatch(string content,
        string oldString, string newString, bool replaceAll)
    {
        var firstIdx = content.IndexOf(oldString, StringComparison.Ordinal);

        if (firstIdx < 0)
            return (null, Error.OldStringNotFound);

        if (!replaceAll)
        {
            var lastIdx = content.LastIndexOf(oldString, StringComparison.Ordinal);
            if (lastIdx != firstIdx)
                return (null, Error.MultipleMatchesFound);
        }

        return (content.Replace(oldString, newString), null);
    }

    private static (string? NewFile, Error? Error) ReplaceLineMatches(string content,
        string oldString, string newString, bool replaceAll)
    {
        var searchLines = oldString.Split('\n');
        if (string.IsNullOrEmpty(searchLines.Last()))
            searchLines = searchLines.Take(searchLines.Length - 1).ToArray();

        if (searchLines.Length == 0)
            return (null, Error.OldStringEmpty);

        var sourceLines = content.Split('\n');
        List<(int Start, int End)> matches = [];

        for (var i = 0; i <= sourceLines.Length - searchLines.Length; i++)
        {
            var matched = true;

            foreach (var (searchLine, j) in searchLines.Select((l, idx) => (l, idx)))
            {
                if (!string.Equals(sourceLines[i + j].TrimEnd('\r'), searchLine.TrimEnd('\r'), StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                matches.Add((i, i + searchLines.Length - 1));
            }
        }

        if (matches.Count == 0)
            return (null, Error.OldStringNotFound);

        if (matches.Count > 1 && !replaceAll)
            return (null, Error.MultipleMatchesFound);

        var sb = new StringBuilder();

        var newLines = newString.Split('\n');
        var newLineSeparator = GetFirstNewLineSeparator(content);

        var matchIdx = 0;
        for (var i = 0; i < sourceLines.Length; i++)
        {
            if (matchIdx >= matches.Count || i < matches[matchIdx].Start)
            {
                sb.Append(sourceLines[i]);
                sb.Append('\n');
                continue;
            }

            if (matchIdx >= matches.Count)
                continue;

            var length = matches[matchIdx].End - matches[matchIdx].Start;
            for (var j = 0; j <= length; j++)
            {
                sb.Append(newLines[j].TrimEnd('\r'));
                sb.Append(newLineSeparator);
            }

            i = matches[matchIdx].End;
            matchIdx++;
        }

        if (!content.EndsWith(newLineSeparator))
        {
            if (matches.Last().End == sourceLines.Length - 1)
                sb.Remove(sb.Length - newLineSeparator.Length, newLineSeparator.Length);
            else
                sb.Remove(sb.Length - 1, 1);
        }

        return (sb.ToString(), null);
    }

    private static string GetFirstNewLineSeparator(string text)
    {
        var crlfIndex = text.IndexOf("\r\n", StringComparison.Ordinal);

        if (crlfIndex == -1)
            return "\n";

        var lfIndex = text.IndexOf('\n');

        if (crlfIndex < lfIndex)
            return "\r\n";

        return "\n";
    }

    public enum Error
    {
        OldStringNotFound,
        MultipleMatchesFound,
        OldStringEmpty,
    }
}