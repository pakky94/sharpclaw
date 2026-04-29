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
                _ => new Dictionary<string, object> { { "error", $"Operation {operation} not supported in Phase 2" } }
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

    private async Task SendRegistration()
    {
        var registration = new Dictionary<string, object>
        {
            { "type", "register" },
            { "bridge_id", _bridgeId },
            { "display_name", "SharpClaw Bridge Client" },
            { "capabilities", new[] { "list_files", "read_file" } },
            { "os", Environment.OSVersion.ToString() },
            { "shell", Environment.OSVersion.Platform == PlatformID.Win32NT ? "cmd.exe" : "/bin/bash" }
        };

        await SendMessage(registration);
        Console.WriteLine("Registration sent.");
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
