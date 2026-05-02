using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SharpClaw.API.Database.Repositories;
using SharpClaw.Common;

namespace SharpClaw.API.Agents.Workspace;

public class BridgeConnectionManager : BackgroundService
{
    private readonly ConcurrentDictionary<string, BridgeConnection> _connections = new();
    private readonly ILogger<BridgeConnectionManager> _logger;
    private readonly WorkspaceRepository _workspaceRepository;

    public BridgeConnectionManager(ILogger<BridgeConnectionManager> logger, WorkspaceRepository workspaceRepository)
    {
        _logger = logger;
        _workspaceRepository = workspaceRepository;
    }

    public async Task HandleBridgeConnection(string bridgeId, WebSocket socket)
    {
        var connection = new BridgeConnection(bridgeId, socket);
        _connections[bridgeId] = connection;

        _logger.LogInformation("Bridge {BridgeId} connected", bridgeId);

        try
        {
            await HandleBridgeMessages(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bridge {BridgeId} connection error", bridgeId);
        }
        finally
        {
            _connections.TryRemove(connection.BridgeId, out _);
            connection.Dispose();
            _logger.LogInformation("Bridge {BridgeId} disconnected", bridgeId);
        }
    }

    private async Task HandleBridgeMessages(BridgeConnection connection)
    {
        var buffer = new byte[4096];

        while (connection.Socket.State == WebSocketState.Open)
        {
            var result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await ProcessBridgeMessage(connection, message);
            }
        }
    }

    private async Task ProcessBridgeMessage(BridgeConnection connection, string message)
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
                    case "register":
                        await HandleRegister(connection, root);
                        break;
                    case "heartbeat":
                        await HandleHeartbeat(connection, root);
                        break;
                    case "response":
                        HandleResponse(connection, root);
                        break;
                    default:
                        _logger.LogWarning("Unknown bridge message type: {Type}", type);
                        break;
                }
            }
            else
            {
                _logger.LogWarning("type property is missing from response: {Message}", message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing bridge message");
        }
    }

    private async Task HandleRegister(BridgeConnection connection, JsonElement root)
    {
        var registration = JsonSerializer.Deserialize<BridgeRegistration>(root.GetRawText());
        if (registration is null) return;

        var newBridgeId = registration.BridgeId;
        if (!string.IsNullOrEmpty(newBridgeId) && newBridgeId != connection.BridgeId)
        {
            _connections.TryRemove(connection.BridgeId, out _);
            connection.BridgeId = newBridgeId;
            _connections[newBridgeId] = connection;
        }

        connection.DisplayName = registration.DisplayName;
        connection.Capabilities = registration.Capabilities ?? [];
        connection.Status = "online";

        _logger.LogInformation("Bridge registered: {BridgeId} ({DisplayName})", registration.BridgeId, registration.DisplayName);

        // Update or create bridge client record in database (including devcontainer info)
        await _workspaceRepository.UpsertBridgeClient(
            registration.BridgeId,
            registration.DisplayName,
            "online",
            registration.IsDevContainer,
            registration.ContainerId,
            registration.WorkspacePathInContainer);

        // Send acknowledgment
        await SendMessage(connection, new { type = "register_ack", status = "ok" });
    }

    private async Task HandleHeartbeat(BridgeConnection connection, JsonElement root)
    {
        connection.LastHeartbeat = DateTime.UtcNow;
        connection.Status = "online";

        await _workspaceRepository.UpdateBridgeStatus(connection.BridgeId, "online");

        await SendMessage(connection, new { type = "heartbeat_ack", timestamp = DateTime.UtcNow });
    }

    private void HandleResponse(BridgeConnection connection, JsonElement root)
    {
        var response = root.Deserialize<BridgeResponse>();

        if (response?.RequestId is null)
        {
            _logger.LogWarning("Invalid response: {Response}", root.GetRawText());
            return;
        }

        if (connection.PendingRequests.TryRemove(response.RequestId, out var tcs))
        {
            tcs.SetResult(response);
        }
        else
        {
            _logger.LogWarning("Received response for unknown request: {RequestId}", response.RequestId);
        }
    }

    public async Task<BridgeResponse?> SendRequest(string bridgeId, BridgeRequest request, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(bridgeId, out var connection))
        {
            return new BridgeResponse
            {
                RequestId = request.RequestId,
                Status = "error",
                ErrorMessage = $"Bridge {bridgeId} not connected."
            };
        }

        var tcs = new TaskCompletionSource<BridgeResponse>();
        connection.PendingRequests[request.RequestId] = tcs;

        try
        {
            await SendMessage(connection, request);

            // Wait for response with timeout
            var timeoutTask = Task.Delay(30000, cancellationToken); // 30 second timeout
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                connection.PendingRequests.TryRemove(request.RequestId, out _);
                return new BridgeResponse
                {
                    RequestId = request.RequestId,
                    Status = "timeout",
                    ErrorMessage = "Request timed out."
                };
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending request to bridge {BridgeId}", bridgeId);
            connection.PendingRequests.TryRemove(request.RequestId, out _);
            return new BridgeResponse
            {
                RequestId = request.RequestId,
                Status = "error",
                ErrorMessage = ex.Message
            };
        }
    }

    private static async Task SendMessage(BridgeConnection connection, object message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public IReadOnlyList<BridgeStatusInfo> GetConnectedBridges()
    {
        return _connections.Values.Select(c => new BridgeStatusInfo
        {
            BridgeId = c.BridgeId,
            DisplayName = c.DisplayName,
            Status = c.Status,
            Capabilities = c.Capabilities,
            LastHeartbeat = c.LastHeartbeat,
            ConnectedAt = c.ConnectedAt
        }).ToArray();
    }

    public void DisconnectBridge(string bridgeId)
    {
        if (_connections.TryRemove(bridgeId, out var connection))
        {
            connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected by server", CancellationToken.None);
            connection.Dispose();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Background task for monitoring bridge health
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(60000, stoppingToken); // Check every minute

                foreach (var connection in _connections.Values)
                {
                    if (DateTime.UtcNow - connection.LastHeartbeat > TimeSpan.FromMinutes(2))
                    {
                        _logger.LogWarning("Bridge {BridgeId} timed out", connection.BridgeId);
                        connection.Status = "offline";
                        await _workspaceRepository.UpdateBridgeStatus(connection.BridgeId, "offline");
                    }
                }
            }
        }, stoppingToken);

        return Task.CompletedTask;
    }
}

public class BridgeConnection : IDisposable
{
    public string BridgeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "online";
    public string[] Capabilities { get; set; } = [];
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public WebSocket Socket { get; }
    public ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> PendingRequests { get; } = new();

    public BridgeConnection(string bridgeId, WebSocket socket)
    {
        BridgeId = bridgeId;
        Socket = socket;
    }

    public void Dispose()
    {
        Socket.Dispose();
    }
}

public class BridgeStatusInfo
{
    public string BridgeId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string[] Capabilities { get; init; } = [];
    public DateTime LastHeartbeat { get; init; }
    public DateTime ConnectedAt { get; init; }
}
