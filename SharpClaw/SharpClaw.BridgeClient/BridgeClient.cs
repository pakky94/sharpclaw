using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SharpClaw.Common;

namespace SharpClaw.BridgeClient;

public class SharpClawBridgeClient
{
    private const int MaxListEntries = 100;
    private Configuration _config;
    private ClientWebSocket? _socket;
    private CancellationTokenSource _cts = new();
    private bool _isConnected;

    public SharpClawBridgeClient(Configuration config)
    {
        _config = config;
    }

    public async Task ConnectAsync()
    {
        _socket = new ClientWebSocket();
        _cts = new CancellationTokenSource();

        await _socket.ConnectAsync(new Uri(_config.ServerUrl), _cts.Token);
        _isConnected = true;

        Console.WriteLine($"Connected to {_config.ServerUrl}");

        await SendRegistration();
    }

    public void Disconnect()
    {
        _isConnected = false;
        _cts.Cancel();
    }

    public async Task RunAsync()
    {
        var buffer = new byte[4096];

        try
        {
            while (_isConnected && _socket?.State == WebSocketState.Open)
            {
                var (messageType, message) = await ReceiveMessage(buffer);

                if (messageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("Server requested close.");
                    break;
                }

                if (messageType == WebSocketMessageType.Text && message is not null)
                    await ProcessMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Bridge operation cancelled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            if (_socket is not null)
            {
                if (_socket.State == WebSocketState.Open)
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                _socket.Dispose();
            }
        }
    }

    private async Task<(WebSocketMessageType messageType, string? message)> ReceiveMessage(byte[] buffer)
    {
        if (_socket is null)
            return (WebSocketMessageType.Close, null);

        using var messageBuffer = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, null);

            if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                messageBuffer.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Text)
                    return (result.MessageType, null);

                var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                return (WebSocketMessageType.Text, message);
            }
        }
    }

    private async Task ProcessMessage(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            Console.WriteLine($"Recieved: {message}");
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();
                switch (type)
                {
                    case "register_ack":
                        Console.WriteLine("Registration acknowledged.");
                        break;
                    case "heartbeat_ack":
                        Console.WriteLine("Heartbeat acknowledged.");
                        break;
                    case "request":
                        await HandleRequest(root);
                        break;
                    default:
                        Console.WriteLine($"Unknown message type: {type}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing message: {ex.Message}");
        }
    }

    private async Task HandleRequest(JsonElement root)
    {
        var request = root.Deserialize<BridgeRequest>();

        if (request is null)
        {
            await SendResponse("unknown", "error", new Dictionary<string, object> { { "error", "Invalid request" } });
            return;
        }

        Console.WriteLine($"Handling operation: {request.Operation} ({request.RequestId})");

        try
        {
            object? result = request.Operation switch
            {
                "list_files" => HandleListFiles(request),
                "read_file" => HandleReadFile(request),
                "write_file" => HandleWriteFile(request),
                "edit_file" => HandleEditFile(request),
                "delete_file" => HandleDeleteFile(request),
                "move_file" => HandleMoveFile(request),
                "make_directory" => HandleMakeDirectory(request),
                "run_command" => await HandleRunCommand(request),
                _ => new Dictionary<string, object> { { "error", $"Operation {request.Operation} not supported" } }
            };

            await SendResponse(request.RequestId, "ok", result);
        }
        catch (Exception ex)
        {
            await SendResponse(request.RequestId, "error", new Dictionary<string, object> { { "error", ex.Message } });
        }
    }

    private static object HandleListFiles(BridgeRequest request)
    {
        var path = request.TryGetStringArg("path") ?? ".";
        var recursive = request.TryGetBoolArg("recursive") is true;
        var includeHidden = request.TryGetBoolArg("include_hidden") is true;

        var rootPath = request.PolicyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!Directory.Exists(resolvedPath))
            return new Dictionary<string, object> { { "error", $"Path does not exist: {path}" } };

        var entries = new List<FileEntry>();
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

                    entries.Add(new FileEntry { type = "directory", name = dirInfo.Name, path = GetRelativePath(rootPath, dir) });
                }

                foreach (var file in Directory.GetFiles(resolvedPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var fileInfo = new FileInfo(file);
                    if (!includeHidden && (fileInfo.Attributes & FileAttributes.Hidden) != 0)
                        continue;

                    entries.Add(new FileEntry { type = "file", name = fileInfo.Name, path = GetRelativePath(rootPath, file), size = fileInfo.Length });
                }

                totalCount = entries.Count;
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

                        entries.Add(new FileEntry { type = "directory", name = dirInfo.Name, path = GetRelativePath(rootPath, dir) });
                        totalCount++;

                        if (entries.Count < MaxListEntries)
                            queue.Enqueue(dir);
                    }

                    foreach (var file in Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        var fileInfo = new FileInfo(file);
                        if (!includeHidden && (fileInfo.Attributes & FileAttributes.Hidden) != 0)
                            continue;

                        entries.Add(new FileEntry { type = "file", name = fileInfo.Name, path = GetRelativePath(rootPath, file), size = fileInfo.Length });
                        totalCount++;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return new Dictionary<string, object> { { "error", $"Access denied: {ex.Message}" } };
        }

        var truncated = recursive && totalCount > MaxListEntries;
        var count = entries.Count;

        var result = new Dictionary<string, object>
        {
            { "title", $"Directory listing: {path}" },
            { "path", path },
            { "recursive", recursive },
            { "entries", entries },
            { "count", count },
            { "total_entries", totalCount },
            { "truncated", truncated }
        };

        if (truncated)
            result["note"] = $"Showing top {MaxListEntries} entries (breadth-first). {totalCount - MaxListEntries} more entries not shown.";

        return result;
    }

    private static object HandleReadFile(BridgeRequest request)
    {
        var path = request.TryGetStringArg("path") ?? "";
        var offset = request.TryGetIntArg("offset") ?? null;
        var length = request.TryGetIntArg("length") ?? null;

        var rootPath = request.PolicyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!File.Exists(resolvedPath))
            return new Dictionary<string, object> { { "error", $"File does not exist: {path}" } };

        var content = File.ReadAllText(resolvedPath);

        if (offset.HasValue || length.HasValue)
        {
            var lines = content.Split('\n').Skip(offset ?? 0);
            if (length.HasValue)
                lines = lines.Take(length.Value);
            content = string.Join('\n', lines);
        }

        return new Dictionary<string, object>
        {
            { "title", $"File read: {path}" },
            { "path", path },
            { "byte_count", Encoding.UTF8.GetByteCount(content) },
            { "content", content }
        };
    }

    private static object HandleWriteFile(BridgeRequest request)
    {
        var path = request.TryGetStringArg("path") ?? "";
        var content = request.TryGetStringArg("content") ?? "";
        var mode = request.TryGetStringArg("mode") ?? "overwrite";

        var rootPath = request.PolicyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        // Check path is allowed (basic policy check)
        if (!IsPathAllowed(resolvedPath, rootPath, request.PolicyContext))
            return new Dictionary<string, object> { { "error", $"Path is denied by policy: {path}" } };

        try
        {
            var parentDir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            FileMode fileMode = mode switch
            {
                "append" => FileMode.Append,
                "create_only" => FileMode.CreateNew,
                _ => FileMode.Create
            };

            if (mode == "create_only" && File.Exists(resolvedPath))
                return new Dictionary<string, object> { { "error", $"File already exists: {path}" } };

            using var stream = new FileStream(resolvedPath, fileMode, FileAccess.Write, FileShare.None);
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);

            return new Dictionary<string, object>
            {
                { "title", $"File written: {path}" },
                { "path", path },
                { "mode", mode },
                { "bytes_written", bytes.Length }
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { { "error", $"Failed to write file: {ex.Message}" } };
        }
    }

    private static object HandleEditFile(BridgeRequest request)
    {
        var path = request.TryGetStringArg("path") ?? "";
        var oldString = request.TryGetStringArg("oldString") ?? "";
        var newString = request.TryGetStringArg("newString") ?? "";
        var replaceAll = request.TryGetBoolArg("replaceAll") is true;

        var rootPath = request.PolicyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!IsPathAllowed(resolvedPath, rootPath, request.PolicyContext))
            return new Dictionary<string, object> { { "error", $"Path is denied by policy: {path}" } };

        if (!File.Exists(resolvedPath))
            return new Dictionary<string, object> { { "error", $"File does not exist: {path}" } };

        try
        {
            var oldFile = File.ReadAllText(resolvedPath);

            if (string.IsNullOrEmpty(oldString))
                return new Dictionary<string, object> { { "error", "oldString cannot be empty" } };

            string newContent;
            if (replaceAll)
            {
                newContent = oldFile.Replace(oldString, newString);
            }
            else
            {
                var index = oldFile.IndexOf(oldString);
                if (index == -1)
                    return new Dictionary<string, object> { { "error", $"oldString not found in file {path}" } };

                // Check for multiple matches
                if (oldFile.IndexOf(oldString, index + 1) != -1)
                    return new Dictionary<string, object> { { "error", $"Multiple matches found in file {path}, use replaceAll=true" } };

                newContent = oldFile.Substring(0, index) + newString + oldFile.Substring(index + oldString.Length);
            }

            File.WriteAllText(resolvedPath, newContent);

            return new Dictionary<string, object>
            {
                { "title", $"File edited: {path}" },
                { "path", path }
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { { "error", $"Failed to edit file: {ex.Message}" } };
        }
    }

    private static object HandleDeleteFile(BridgeRequest request)
    {
        var path = request.TryGetStringArg("path") ?? "";
        var recursive = request.TryGetBoolArg("recursive") is true;

        var rootPath = request.PolicyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!IsPathAllowed(resolvedPath, rootPath, request.PolicyContext))
            return new Dictionary<string, object> { { "error", $"Path is denied by policy: {path}" } };

        try
        {
            if (Directory.Exists(resolvedPath))
            {
                if (!recursive)
                    return new Dictionary<string, object> { { "error", $"Path is a directory, use recursive=true: {path}" } };

                Directory.Delete(resolvedPath, true);
            }
            else if (File.Exists(resolvedPath))
            {
                File.Delete(resolvedPath);
            }
            else
            {
                return new Dictionary<string, object> { { "error", $"Path does not exist: {path}" } };
            }

            return new Dictionary<string, object>
            {
                { "title", $"Deleted: {path}" },
                { "path", path },
                { "recursive", recursive }
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { { "error", $"Failed to delete: {ex.Message}" } };
        }
    }

    private static object HandleMoveFile(BridgeRequest request)
    {
        var source = request.TryGetStringArg("source") ?? "";
        var destination = request.TryGetStringArg("destination") ?? "";

        var rootPath = request.PolicyContext.RootPath;
        var resolvedSource = Path.IsPathRooted(source) ? source : Path.Combine(rootPath, source);
        var resolvedDest = Path.IsPathRooted(destination) ? destination : Path.Combine(rootPath, destination);

        if (!IsPathAllowed(resolvedSource, rootPath, request.PolicyContext))
            return new Dictionary<string, object> { { "error", $"Source path denied by policy: {source}" } };

        if (!IsPathAllowed(resolvedDest, rootPath, request.PolicyContext))
            return new Dictionary<string, object> { { "error", $"Destination path denied by policy: {destination}" } };

        if (!File.Exists(resolvedSource) && !Directory.Exists(resolvedSource))
            return new Dictionary<string, object> { { "error", $"Source does not exist: {source}" } };

        try
        {
            var parentDir = Path.GetDirectoryName(resolvedDest);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            if (Directory.Exists(resolvedSource))
                Directory.Move(resolvedSource, resolvedDest);
            else
                File.Move(resolvedSource, resolvedDest);

            return new Dictionary<string, object>
            {
                { "title", $"Moved: {source} -> {destination}" },
                { "source", source },
                { "destination", destination }
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { { "error", $"Failed to move: {ex.Message}" } };
        }
    }

    private static object HandleMakeDirectory(BridgeRequest request)
    {
        var path = request.TryGetStringArg("path") ?? "";

        var rootPath = request.PolicyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!IsPathAllowed(resolvedPath, rootPath, request.PolicyContext))
            return new Dictionary<string, object> { { "error", $"Path is denied by policy: {path}" } };

        if (Directory.Exists(resolvedPath))
            return new Dictionary<string, object> { { "error", $"Directory already exists: {path}" } };

        try
        {
            Directory.CreateDirectory(resolvedPath);

            return new Dictionary<string, object>
            {
                { "title", $"Directory created: {path}" },
                { "path", path }
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { { "error", $"Failed to create directory: {ex.Message}" } };
        }
    }

    private static bool IsPathAllowed(string fullPath, string rootPath, BridgePolicyContext policyContext)
    {
        // Basic check: path must be within root path
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            return false;

        // TODO: implement proper allowlist/denylist checking here
        // For now, just check the path is within root

        return true;
    }

    private static async Task<object> HandleRunCommand(BridgeRequest request)
    {
        var command = request.TryGetStringArg("command") ?? "";
        var cwd = request.TryGetStringArg("cwd") ?? ".";
        var timeoutMs = request.TryGetIntArg("timeout_ms") ?? 30000;

        var rootPath = request.PolicyContext.RootPath;
        var resolvedCwd = Path.IsPathRooted(cwd) ? cwd : Path.Combine(rootPath, cwd);

        if (!Directory.Exists(resolvedCwd))
            return new Dictionary<string, object> { { "error", $"Working directory does not exist: {cwd}" } };

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

            var exited = await Task.Run(() => process.WaitForExit(timeoutMs));

            if (!exited)
            {
                try { process.Kill(); } catch { }
                return new Dictionary<string, object>
                {
                    { "title", $"Command timed out: {command}" },
                    { "command", command },
                    { "timeout_ms", timeoutMs },
                    { "error", $"Command exceeded {timeoutMs}ms timeout and was terminated." },
                    { "killed", true }
                };
            }

            var stdout = outputBuilder.ToString();
            var stderr = errorBuilder.ToString();

            return new Dictionary<string, object>
            {
                { "title", $"Command executed: {command}" },
                { "command", command },
                { "cwd", resolvedCwd },
                { "exit_code", process.ExitCode },
                { "stdout", string.IsNullOrWhiteSpace(stdout) ? string.Empty : stdout },
                { "stderr", string.IsNullOrWhiteSpace(stderr) ? string.Empty : stderr },
                { "duration_ms", (int)(process.ExitTime - process.StartTime).TotalMilliseconds }
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { { "error", $"Command execution failed: {ex.Message}" } };
        }
    }

    private static (bool isDevContainer, string? containerId, string? workspacePathInContainer) DetectDevContainer()
    {
        // Check for Docker container indicators
        var containerId = Environment.GetEnvironmentVariable("DOCKER_CONTAINER");
        if (string.IsNullOrEmpty(containerId))
            containerId = Environment.GetEnvironmentVariable("DEVCONTAINER_IPC");

        // Check for .dockerenv file (Linux containers)
        if (string.IsNullOrEmpty(containerId) && File.Exists("/.dockerenv"))
        {
            // Try to get container ID from hostname (often the short container ID)
            try { containerId = Environment.MachineName; } catch { }
        }

        // Check for /proc/1/cgroup (Linux container)
        if (string.IsNullOrEmpty(containerId) && File.Exists("/proc/1/cgroup"))
        {
            try
            {
                var cgroup = File.ReadAllText("/proc/1/cgroup");
                if (cgroup.Contains("docker") || cgroup.Contains("containerd"))
                    containerId = "detected";
            }
            catch { }
        }

        var isDevContainer = !string.IsNullOrEmpty(containerId);

        // Determine workspace path in container (default to root path from environment or current directory)
        string? workspacePathInContainer = null;
        if (isDevContainer)
        {
            // Try to get workspace path from environment variable set by devcontainer CLI
            workspacePathInContainer = Environment.GetEnvironmentVariable("DEVCONTAINER_WORKSPACE_FOLDER")
                ?? Environment.GetEnvironmentVariable("WORKSPACE_FOLDER");
        }

        return (isDevContainer, containerId, workspacePathInContainer);
    }

    private async Task SendRegistration()
    {
        var (isDevContainer, containerId, workspacePathInContainer) = DetectDevContainer();

        var registration = new BridgeRegistration
        {
            BridgeId = _config.BridgeId,
            DisplayName = isDevContainer ? $"DevContainer Bridge ({Environment.MachineName})" : _config.DisplayName ?? "SharpClaw Bridge Client",
            Capabilities = ["list_files", "read_file", "write_file", "edit_file", "delete_file", "move_file", "make_directory", "run_command"],
            Os = Environment.OSVersion.ToString(),
            Shell = Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "/bin/bash",
            IsDevContainer = isDevContainer,
            ContainerId = containerId ?? "",
            WorkspacePathInContainer = workspacePathInContainer ?? "",
        };

        await SendMessage(registration);
        Console.WriteLine($"Registration sent. DevContainer: {isDevContainer}, ContainerId: {containerId}");
    }

    private async Task SendHeartbeat()
    {
        var heartbeat = new BridgeHeartbeat
        {
            BridgeId = _config.BridgeId,
            Timestamp = DateTime.UtcNow,
        };

        await SendMessage(heartbeat);
    }

    private async Task SendResponse(string requestId, string status, object? result)
    {
        Console.WriteLine($"Response: {requestId} {status} res: {result}");
        var response = new BridgeResponse
        {
            RequestId = requestId,
            Status = status,
            Result = result,
        };

        await SendMessage(response);
    }

    private async Task SendMessage(object message)
    {
        if (_socket?.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
    }

    private static string GetRelativePath(string rootPath, string fullPath)
    {
        if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath[rootPath.Length..].TrimStart(Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? "." : relative;
        }
        return fullPath;
    }
}

public class FileEntry
{
    public string type { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
    public string path { get; set; } = string.Empty;
    public long? size { get; set; }
}
