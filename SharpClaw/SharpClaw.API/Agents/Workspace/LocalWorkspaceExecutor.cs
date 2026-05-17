using System.Diagnostics;
using System.Text;
using SharpClaw.Common;

namespace SharpClaw.API.Agents.Workspace;

public class LocalWorkspaceExecutor : IWorkspaceExecutionRouter
{
    private const int MaxReadSize = 40_000;
    private const int MaxListEntries = 100;

    public Task<object> ListFiles(ResolvedWorkspace workspace, string path, bool recursive = false, bool includeHidden = false)
    {
        var resolvedPath = PathContainment.NormalizePath(workspace.RootPath, path);

        if (!Directory.Exists(resolvedPath))
            return Task.FromResult<object>(new { error = $"Path does not exist: {path}" });

        var entries = new List<object>();
        var totalCount = 0;

        try
        {
            if (!recursive)
            {
                foreach (var dir in Directory.GetDirectories(resolvedPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (!includeHidden && (dirInfo.Attributes & FileAttributes.Hidden) != 0)
                        continue;
                    var relative = MakeRelative(workspace.RootPath, dir);
                    entries.Add(new { type = "directory", path = relative, name = dirInfo.Name });
                }

                foreach (var file in Directory.GetFiles(resolvedPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var fileInfo = new FileInfo(file);
                    if (!includeHidden && (fileInfo.Attributes & FileAttributes.Hidden) != 0)
                        continue;
                    var relative = MakeRelative(workspace.RootPath, file);
                    entries.Add(new { type = "file", path = relative, name = fileInfo.Name, size = fileInfo.Length });
                }
            }
            else
            {
                var queue = new Queue<string>();
                queue.Enqueue(resolvedPath);

                while (queue.Count > 0 && entries.Count < MaxListEntries)
                {
                    var current = queue.Dequeue();

                    foreach (var dir in Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (!includeHidden && (dirInfo.Attributes & FileAttributes.Hidden) != 0)
                            continue;
                        var relative = MakeRelative(workspace.RootPath, dir);
                        entries.Add(new { type = "directory", path = relative, name = dirInfo.Name });
                        totalCount++;
                        if (entries.Count < MaxListEntries)
                            queue.Enqueue(dir);
                    }

                    foreach (var file in Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        var fileInfo = new FileInfo(file);
                        if (!includeHidden && (fileInfo.Attributes & FileAttributes.Hidden) != 0)
                            continue;
                        var relative = MakeRelative(workspace.RootPath, file);
                        entries.Add(new { type = "file", path = relative, name = fileInfo.Name, size = fileInfo.Length });
                        totalCount++;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult<object>(new { error = $"Access denied: {ex.Message}" });
        }

        var truncated = recursive && totalCount > MaxListEntries;

        return Task.FromResult<object>(new
        {
            title = $"Directory listing: {path}",
            path,
            recursive,
            entries,
            count = entries.Count,
            total_entries = totalCount,
            truncated,
            note = truncated ? $"Showing top {MaxListEntries} entries (breadth-first). {totalCount - MaxListEntries} more entries not shown." : null,
        });
    }

    public async Task<object> ReadFile(ResolvedWorkspace workspace, string path, int? offset = null, int? length = null)
    {
        var resolvedPath = PathContainment.NormalizePath(workspace.RootPath, path);

        if (!File.Exists(resolvedPath))
            return new { error = $"File does not exist: {path}" };

        try
        {
            var content = await File.ReadAllTextAsync(resolvedPath);
            if (offset.HasValue || length.HasValue)
            {
                var lines = content.Split('\n').Skip(offset ?? 0);
                if (length.HasValue)
                    lines = lines.Take(length.Value);
                content = string.Join('\n', lines);
            }

            var byteCount = Encoding.UTF8.GetByteCount(content);

            if (byteCount > MaxReadSize)
            {
                return new
                {
                    title = $"File read (truncated): {path}",
                    path,
                    byte_count = byteCount,
                    truncated = true,
                    content = content[..Math.Min(content.Length, MaxReadSize)],
                    note = $"File exceeds {MaxReadSize} bytes. Use offset/length parameters to read specific portions.",
                };
            }

            return new
            {
                title = $"File read: {path}",
                path,
                byte_count = byteCount,
                content,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to read file: {ex.Message}" };
        }
    }

    public async Task<object> WriteFile(ResolvedWorkspace workspace, string path, string content, string mode = "overwrite")
    {
        var resolvedPath = PathContainment.NormalizePath(workspace.RootPath, path);

        if (!PathContainment.IsPathAllowed(workspace.RootPath, resolvedPath, workspace.AllowlistPatterns, workspace.DenylistPatterns))
            return new { error = $"Path is denied by workspace policy: {path}" };

        try
        {
            var parentDir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            FileMode fileMode;
            switch (mode)
            {
                case "append":
                    fileMode = FileMode.Append;
                    break;
                case "create_only":
                    if (File.Exists(resolvedPath))
                        return new { error = $"File already exists and mode is create_only: {path}" };
                    fileMode = FileMode.CreateNew;
                    break;
                default:
                    fileMode = FileMode.Create;
                    break;
            }

            await using var stream = new FileStream(resolvedPath, fileMode, FileAccess.Write, FileShare.None);
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes);

            return new
            {
                title = $"File written: {path}",
                path,
                mode,
                bytes_written = Encoding.UTF8.GetByteCount(content),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to write file: {ex.Message}" };
        }
    }

    public async Task<object> EditFile(ResolvedWorkspace workspace, string path, string oldString, string newString, bool replaceAll = false)
    {
        var resolvedPath = PathContainment.NormalizePath(workspace.RootPath, path);

        if (!PathContainment.IsPathAllowed(workspace.RootPath, resolvedPath, workspace.AllowlistPatterns, workspace.DenylistPatterns))
            return new { error = $"Path is denied by workspace policy: {path}" };

        if (!File.Exists(resolvedPath))
            return new { error = $"File does not exist: {path}" };

        try
        {
            var oldFile = await File.ReadAllTextAsync(resolvedPath);

            var (newContent, error) = StringReplacer.Replace(oldFile, oldString, newString, replaceAll);

            if (error is not null)
                return new
                {
                    error = error switch
                    {
                        StringReplacer.Error.OldStringNotFound => $"oldString not found in file {path}",
                        StringReplacer.Error.MultipleMatchesFound => $"multiple matches of oldString found in file {path}, to replace all occurrences call this tool with replaceAll=true",
                        _ => $"Error replacing string in '{path}'",
                    }
                };

            await File.WriteAllTextAsync(resolvedPath, newContent);

            return new
            {
                title = $"File edited: {path}",
                path,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to write file: {ex.Message}" };
        }
    }

    public Task<object> DeleteFile(ResolvedWorkspace workspace, string path, bool recursive = false)
    {
        var resolvedPath = PathContainment.NormalizePath(workspace.RootPath, path);

        if (!PathContainment.IsPathAllowed(workspace.RootPath, resolvedPath, workspace.AllowlistPatterns, workspace.DenylistPatterns))
            return Task.FromResult<object>(new { error = $"Path is denied by workspace policy: {path}" });

        try
        {
            if (Directory.Exists(resolvedPath))
            {
                if (!recursive)
                    return Task.FromResult<object>(new { error = $"Path is a directory. Use recursive=true to delete: {path}" });
                Directory.Delete(resolvedPath, true);
            }
            else if (File.Exists(resolvedPath))
            {
                File.Delete(resolvedPath);
            }
            else
            {
                return Task.FromResult<object>(new { error = $"Path does not exist: {path}" });
            }

            return Task.FromResult<object>(new
            {
                title = $"Deleted: {path}",
                path,
                recursive,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { error = $"Failed to delete: {ex.Message}" });
        }
    }

    public Task<object> MoveFile(ResolvedWorkspace workspace, string source, string destination)
    {
        var resolvedSource = PathContainment.NormalizePath(workspace.RootPath, source);
        var resolvedDest = PathContainment.NormalizePath(workspace.RootPath, destination);

        if (!PathContainment.IsPathAllowed(workspace.RootPath, resolvedSource, workspace.AllowlistPatterns, workspace.DenylistPatterns))
            return Task.FromResult<object>(new { error = $"Source path is denied by workspace policy: {source}" });

        if (!PathContainment.IsPathAllowed(workspace.RootPath, resolvedDest, workspace.AllowlistPatterns, workspace.DenylistPatterns))
            return Task.FromResult<object>(new { error = $"Destination path is denied by workspace policy: {destination}" });

        if (!File.Exists(resolvedSource) && !Directory.Exists(resolvedSource))
            return Task.FromResult<object>(new { error = $"Source does not exist: {source}" });

        try
        {
            var parentDir = Path.GetDirectoryName(resolvedDest);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            if (Directory.Exists(resolvedSource))
                Directory.Move(resolvedSource, resolvedDest);
            else
                File.Move(resolvedSource, resolvedDest);

            return Task.FromResult<object>(new
            {
                title = $"Moved: {source} -> {destination}",
                source,
                destination,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { error = $"Failed to move: {ex.Message}" });
        }
    }

    public Task<object> MakeDirectory(ResolvedWorkspace workspace, string path)
    {
        var resolvedPath = PathContainment.NormalizePath(workspace.RootPath, path);

        if (!PathContainment.IsPathAllowed(workspace.RootPath, resolvedPath, workspace.AllowlistPatterns, workspace.DenylistPatterns))
            return Task.FromResult<object>(new { error = $"Path is denied by workspace policy: {path}" });

        try
        {
            if (Directory.Exists(resolvedPath))
                return Task.FromResult<object>(new { error = $"Directory already exists: {path}" });

            Directory.CreateDirectory(resolvedPath);

            return Task.FromResult<object>(new
            {
                title = $"Directory created: {path}",
                path,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { error = $"Failed to create directory: {ex.Message}" });
        }
    }

    public async Task<object> RunCommand(ResolvedWorkspace workspace, string command, int? timeoutMs = null, int? maxOutputBytes = null, Dictionary<string, string>? env = null)
    {
        var resolvedCwd = workspace.RootPath;
        var timeout = timeoutMs ?? 30000;
        timeout = Math.Min(timeout, 120_000);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c \"{command}\"" : $"-c \"{command}\"",
                WorkingDirectory = resolvedCwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Inject secrets as environment variables
            if (env is not null)
            {
                foreach (var (key, value) in env)
                    psi.Environment[key] = value;
            }

            using var process = new Process { StartInfo = psi };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exited = await Task.Run(() => process.WaitForExit(timeout));

            if (!exited)
            {
                try { process.Kill(); } catch { }
                return new
                {
                    title = $"Command timed out: {command}",
                    command,
                    timeout_ms = timeout,
                    error = $"Command exceeded {timeout}ms timeout and was terminated.",
                    killed = true,
                };
            }

            var stdout = outputBuilder.ToString();
            var stderr = errorBuilder.ToString();

            if (maxOutputBytes.HasValue)
            {
                var maxBytes = maxOutputBytes.Value;
                if (Encoding.UTF8.GetByteCount(stdout) > maxBytes)
                    stdout = stdout[..Math.Min(stdout.Length, maxOutputBytes.Value / 2)] + "\n...[truncated]";
                if (Encoding.UTF8.GetByteCount(stderr) > maxBytes)
                    stderr = stderr[..Math.Min(stderr.Length, maxOutputBytes.Value / 2)] + "\n...[truncated]";
            }

            return new
            {
                title = $"Command executed: {command}",
                command,
                cwd = resolvedCwd,
                exit_code = process.ExitCode,
                stdout = string.IsNullOrWhiteSpace(stdout) ? null : stdout,
                stderr = string.IsNullOrWhiteSpace(stderr) ? null : stderr,
                duration_ms = (int)(process.ExitTime - process.StartTime).TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Command execution failed: {ex.Message}" };
        }
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
}
