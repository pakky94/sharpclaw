using System.Text.Json;
using SharpClaw.BridgeClient;

var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sharpclaw", "config.json");
Console.WriteLine($"Config path: {configPath}");

Configuration? config = null;

if (Path.Exists(configPath))
{
    var configContent = File.ReadAllText(configPath);
    config = JsonSerializer.Deserialize<Configuration>(configContent, new JsonSerializerOptions
    {
        AllowTrailingCommas = true,
    });
    Console.WriteLine($"Configuration loaded from {configPath}");
}

if (config is null)
{
    var bridgeId = args.Length > 0 ? args[0] : Guid.NewGuid().ToString("N");
    var serverUrl = args.Length > 1 ? args[1] : "ws://localhost:5846/bridge/connect";

    config = new Configuration
    {
        BridgeId = bridgeId,
        ServerUrl = serverUrl,
    };
    var configContent = JsonSerializer.Serialize(config, new JsonSerializerOptions
    {
        WriteIndented = true,
    });
    File.WriteAllText(configPath, configContent);
    Console.WriteLine($"Configuration saved to {configPath}");
}

Console.WriteLine($"Bridge Client starting...");
Console.WriteLine($"Bridge ID: {config.BridgeId}");
Console.WriteLine($"Server URL: {config.ServerUrl}");

var client = new SharpClawBridgeClient(config);
await client.ConnectAsync();

Console.WriteLine("Bridge connected. Press Ctrl+C to exit.");
Console.CancelKeyPress += (_, _) => client.Disconnect();

await client.RunAsync();
Console.WriteLine("Bridge disconnected.");

public class Configuration
{
    public required string BridgeId { get; set; }
    public required string ServerUrl { get; set; }
    public string? DisplayName { get; set; }
}