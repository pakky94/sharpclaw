using System.Net.WebSockets;
using System.Text.Json;
using SharpClaw.Common;

namespace SharpClaw.BridgeClient;

public enum BridgeStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

public class BridgeClientService
{
    private readonly Configuration _config;
    private SharpClawBridgeClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private BridgeStatus _status = BridgeStatus.Disconnected;

    public BridgeStatus Status => _status;
    public Configuration Config => _config;

    public event Action<BridgeStatus>? OnStatusChanged;

    public BridgeClientService(Configuration config)
    {
        _config = config;
    }

    public async Task ConnectAsync()
    {
        if (_status is BridgeStatus.Connected or BridgeStatus.Connecting)
            return;

        SetStatus(BridgeStatus.Connecting);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Start auto-reconnect loop
        _runTask = RunWithReconnectAsync(token);
        await WaitForConnectionAsync(token);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _client?.Disconnect();
        _client = null;
        SetStatus(BridgeStatus.Disconnected);
    }

    private async Task RunWithReconnectAsync(CancellationToken token)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        const int maxRetryDelaySeconds = 30;

        while (!token.IsCancellationRequested)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Attempting to connect...");
                _client = new SharpClawBridgeClient(_config);
                await _client.ConnectAsync();
                SetStatus(BridgeStatus.Connected);
                retryDelay = TimeSpan.FromSeconds(1); // Reset on successful connect

                await _client.RunAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                // Connection failed, will retry
                System.Diagnostics.Debug.WriteLine("WebSocket connection failed, retrying...");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Bridge error: {ex.Message}");
            }

            if (token.IsCancellationRequested)
                break;

            SetStatus(BridgeStatus.Reconnecting);

            try
            {
                await Task.Delay(retryDelay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Exponential backoff
            retryDelay = TimeSpan.FromSeconds(
                Math.Min(retryDelay.TotalSeconds * 2, maxRetryDelaySeconds));
        }
    }

    private async Task WaitForConnectionAsync(CancellationToken token)
    {
        // Wait up to 5 seconds for initial connection
        for (var i = 0; i < 50; i++)
        {
            if (token.IsCancellationRequested)
                return;

            if (_status == BridgeStatus.Connected)
                return;

            await Task.Delay(100, token);
        }
    }

    private void SetStatus(BridgeStatus status)
    {
        if (_status == status)
            return;

        _status = status;
        OnStatusChanged?.Invoke(status);
    }
}
