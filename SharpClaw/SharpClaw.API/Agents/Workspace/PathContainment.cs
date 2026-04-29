using System.Text.RegularExpressions;

namespace SharpClaw.API.Agents.Workspace;

public static partial class PathContainment
{
    public static string NormalizePath(string workspaceRoot, string relativeOrAbsolutePath)
    {
        if (relativeOrAbsolutePath == ".")
            relativeOrAbsolutePath = string.Empty;

        var resolved = Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(workspaceRoot, relativeOrAbsolutePath);

        var fullPath = Path.GetFullPath(resolved);
        var normalizedRoot = Path.GetFullPath(workspaceRoot);

        if (!fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path '{fullPath}' is outside workspace root '{normalizedRoot}'.");
        }

        return fullPath;
    }

    public static string NormalizeCwd(string workspaceRoot, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || cwd == ".")
            return Path.GetFullPath(workspaceRoot);
        return NormalizePath(workspaceRoot, cwd);
    }

    public static bool IsPathAllowed(string workspaceRoot, string path, string[] allowlist, string[] denylist)
    {
        var relative = MakeRelative(workspaceRoot, path);

        foreach (var pattern in denylist)
        {
            if (MatchesPattern(relative, pattern))
                return false;
        }

        if (allowlist.Length > 0)
        {
            foreach (var pattern in allowlist)
            {
                if (MatchesPattern(relative, pattern))
                    return true;
            }
            return false;
        }

        return true;
    }

    public static bool IsSymlinkSafe(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            return false;

        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.Exists && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            return false;

        return true;
    }

    private static string MakeRelative(string workspaceRoot, string fullPath)
    {
        var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(fullPath);

        if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedPath[normalizedRoot.Length..].TrimStart(Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? "." : relative;
        }

        return normalizedPath;
    }

    [GeneratedRegex(@"\*+", RegexOptions.Compiled)]
    private static partial Regex WildcardRegex();

    private static bool MatchesPattern(string path, string pattern)
    {
        if (path.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            return true;

        var regexPattern = WildcardRegex().Replace(Regex.Escape(pattern), ".*");
        return Regex.IsMatch(path, "^" + regexPattern + "$", RegexOptions.IgnoreCase);
    }
}
