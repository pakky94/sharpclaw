using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.BridgeClient;

public class SharpClawBridgeClient
{
    private readonly string _bridgeId;
    private ClientWebSocket? _socket;
    private CancellationTokenSource _cts = new();
    private bool _isConnected;

    public SharpClawBridgeClient(string bridgeId)
    {
        _bridgeId = bridgeId;
    }

    public async Task ConnectAsync(string serverUrl)
    {
        _socket = new ClientWebSocket();
        _cts = new CancellationTokenSource();

        await _socket.ConnectAsync(new Uri(serverUrl), _cts.Token);
        _isConnected = true;

        Console.WriteLine($"Connected to {serverUrl}");

        // Send registration
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
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("Server requested close.");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessMessage(message);
                }
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

    private async Task ProcessMessage(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
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
        if (!root.TryGetProperty("request_id", out var requestIdProp) ||
            !root.TryGetProperty("operation", out var operationProp))
            return;

        var requestId = requestIdProp.GetString() ?? Guid.NewGuid().ToString();
        var operation = operationProp.GetString() ?? "unknown";
        
        var args = new Dictionary<string, object?>();
        if (root.TryGetProperty("args", out var argsProp))
        {
            foreach (var prop in argsProp.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetInt32(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }
        }

        var policyContext = new BridgePolicyContext();
        if (root.TryGetProperty("policy_context", out var policyProp))
        {
            if (policyProp.TryGetProperty("root_path", out var rootPathProp))
                policyContext.RootPath = rootPathProp.GetString() ?? "";
            if (policyProp.TryGetProperty("policy_mode", out var policyModeProp))
                policyContext.PolicyMode = policyModeProp.GetString() ?? "";
        }

        Console.WriteLine($"Handling operation: {operation}");

        try
        {
            object? result = operation switch
            {
                "list_files" => HandleListFiles(args, policyContext),
                "read_file" => HandleReadFile(args, policyContext),
                "write_file" => HandleWriteFile(args, policyContext),
                "edit_file" => HandleEditFile(args, policyContext),
                "delete_file" => HandleDeleteFile(args, policyContext),
                "move_file" => HandleMoveFile(args, policyContext),
                "make_directory" => HandleMakeDirectory(args, policyContext),
                "run_command" => await HandleRunCommand(args, policyContext),
                _ => new Dictionary<string, object> { { "error", $"Operation {operation} not supported" } }
            };

            await SendResponse(requestId, "ok", result);
        }
        catch (Exception ex)
        {
            await SendResponse(requestId, "error", new Dictionary<string, object> { { "error", ex.Message } });
        }
    }

    private object HandleListFiles(Dictionary<string, object?> args, BridgePolicyContext policyContext)
    {
        var path = args.GetValueOrDefault("path") as string ?? ".";
        var recursive = args.GetValueOrDefault("recursive") is true;
        var includeHidden = args.GetValueOrDefault("include_hidden") is true;

        var rootPath = policyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!Directory.Exists(resolvedPath))
            return new Dictionary<string, object> { { "error", $"Path does not exist: {path}" } };

        var entries = new List<FileEntry>();
        var count = 0;

        if (!recursive)
        {
            foreach (var dir in Directory.GetDirectories(resolvedPath))
            {
                var dirInfo = new DirectoryInfo(dir);
                if (!includeHidden && (dirInfo.Attributes & FileAttributes.Hidden) != 0) continue;
                entries.Add(new FileEntry { type = "directory", name = dirInfo.Name, path = GetRelativePath(rootPath, dir) });
                count++;
            }

            foreach (var file in Directory.GetFiles(resolvedPath))
            {
                var fileInfo = new FileInfo(file);
                if (!includeHidden && (fileInfo.Attributes & FileAttributes.Hidden) != 0) continue;
                entries.Add(new FileEntry { type = "file", name = fileInfo.Name, path = GetRelativePath(rootPath, file), size = fileInfo.Length });
                count++;
            }
        }

        return new Dictionary<string, object>
        {
            { "title", $"Directory listing: {path}" },
            { "path", path },
            { "recursive", recursive },
            { "entries", entries },
            { "count", count },
            { "total_entries", count },
            { "truncated", false }
        };
    }

    private object HandleReadFile(Dictionary<string, object?> args, BridgePolicyContext policyContext)
    {
        var path = args.GetValueOrDefault("path") as string ?? "";
        var offset = args.GetValueOrDefault("offset") is int off ? off : (int?)null;
        var length = args.GetValueOrDefault("length") is int len ? len : (int?)null;

        var rootPath = policyContext.RootPath;
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

    private object HandleWriteFile(Dictionary<string, object?> args, BridgePolicyContext policyContext)
    {
        var path = args.GetValueOrDefault("path") as string ?? "";
        var content = args.GetValueOrDefault("content") as string ?? "";
        var mode = args.GetValueOrDefault("mode") as string ?? "overwrite";

        var rootPath = policyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        // Check path is allowed (basic policy check)
        if (!IsPathAllowed(resolvedPath, rootPath, policyContext))
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

    private object HandleEditFile(Dictionary<string, object?> args, BridgePolicyContext policyContext)
    {
        var path = args.GetValueOrDefault("path") as string ?? "";
        var oldString = args.GetValueOrDefault("oldString") as string ?? "";
        var newString = args.GetValueOrDefault("newString") as string ?? "";
        var replaceAll = args.GetValueOrDefault("replaceAll") is true;

        var rootPath = policyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!IsPathAllowed(resolvedPath, rootPath, policyContext))
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

    private object HandleDeleteFile(Dictionary<string, object?> args, BridgePolicyContext policyContext)
    {
        var path = args.GetValueOrDefault("path") as string ?? "";
        var recursive = args.GetValueOrDefault("recursive") is true;

        var rootPath = policyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!IsPathAllowed(resolvedPath, rootPath, policyContext))
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

    private object HandleMoveFile(Dictionary<string, object?> args, BridgePolicyContext policyContext)
    {
        var source = args.GetValueOrDefault("source") as string ?? "";
        var destination = args.GetValueOrDefault("destination") as string ?? "";

        var rootPath = policyContext.RootPath;
        var resolvedSource = Path.IsPathRooted(source) ? source : Path.Combine(rootPath, source);
        var resolvedDest = Path.IsPathRooted(destination) ? destination : Path.Combine(rootPath, destination);

        if (!IsPathAllowed(resolvedSource, rootPath, policyContext))
            return new Dictionary<string, object> { { "error", $"Source path denied by policy: {source}" } };

        if (!IsPathAllowed(resolvedDest, rootPath, policyContext))
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

    private object HandleMakeDirectory(Dictionary<string, object?> args, BridgePolicyContext policyContext)
    {
        var path = args.GetValueOrDefault("path") as string ?? "";

        var rootPath = policyContext.RootPath;
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);

        if (!IsPathAllowed(resolvedPath, rootPath, policyContext))
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

    private async Task<object> HandleRunCommand(Dictionary<string, object?> args, BridgePolicyContext policyContext)
    {
        var command = args.GetValueOrDefault("command") as string ?? "";
        var cwd = args.GetValueOrDefault("cwd") as string ?? ".";
        var timeoutMs = args.GetValueOrDefault("timeout_ms") is int t ? t : 30000;

        var rootPath = policyContext.RootPath;
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
                { "stdout", string.IsNullOrWhiteSpace(stdout) ? null : stdout },
                { "stderr", string.IsNullOrWhiteSpace(stderr) ? null : stderr },
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

        var registration = new Dictionary<string, object>
        {
            { "type", "register" },
            { "bridge_id", _bridgeId },
            { "display_name", isDevContainer ? $"DevContainer Bridge ({Environment.MachineName})" : "SharpClaw Bridge Client" },
            { "capabilities", new[] { "list_files", "read_file", "write_file", "edit_file", "delete_file", "move_file", "make_directory", "run_command" } },
            { "os", Environment.OSVersion.ToString() },
            { "shell", Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "/bin/bash" },
            { "is_devcontainer", isDevContainer },
            { "container_id", containerId ?? "" },
            { "workspace_path_in_container", workspacePathInContainer ?? "" }
        };

        await SendMessage(registration);
        Console.WriteLine($"Registration sent. DevContainer: {isDevContainer}, ContainerId: {containerId}");
    }

    private async Task SendHeartbeat()
    {
        var heartbeat = new Dictionary<string, object>
        {
            { "type", "heartbeat" },
            { "bridge_id", _bridgeId },
            { "timestamp", DateTime.UtcNow }
        };

        await SendMessage(heartbeat);
    }

    private async Task SendResponse(string requestId, string status, object? result)
    {
        var response = new Dictionary<string, object?>
        {
            { "type", "response" },
            { "request_id", requestId },
            { "status", status },
            { "result", result }
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
