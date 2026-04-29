using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.BridgeClient;

var bridgeId = args.Length > 0 ? args[0] : Guid.NewGuid().ToString("N");
var serverUrl = args.Length > 1 ? args[1] : "ws://localhost:5846/bridge/connect";

Console.WriteLine($"Bridge Client starting...");
Console.WriteLine($"Bridge ID: {bridgeId}");
Console.WriteLine($"Server URL: {serverUrl}");

var client = new SharpClawBridgeClient(bridgeId);
await client.ConnectAsync(serverUrl);

Console.WriteLine("Bridge connected. Press Ctrl+C to exit.");
Console.CancelKeyPress += (_, _) => client.Disconnect();

await client.RunAsync();
Console.WriteLine("Bridge disconnected.");
