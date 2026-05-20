using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SharpClaw.BridgeClient;

namespace SharpClaw.BridgeClient;

public partial class App : Application
{
    private TrayIconManager? _trayIconManager;
    private BridgeClientService? _bridgeService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var config = LoadConfig();
            _bridgeService = new BridgeClientService(config);
            _trayIconManager = new TrayIconManager(_bridgeService);

            _bridgeService.OnStatusChanged += status =>
                _trayIconManager.UpdateStatus(status);

            // Auto-connect on startup
            _ = _bridgeService.ConnectAsync();

            desktop.Exit += (_, _) =>
            {
                _bridgeService.Disconnect();
                _trayIconManager.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Configuration LoadConfig()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "sharpclaw", "config.json");

        if (Path.Exists(configPath))
        {
            var configContent = File.ReadAllText(configPath);
            var config = System.Text.Json.JsonSerializer.Deserialize<Configuration>(configContent,
                new System.Text.Json.JsonSerializerOptions { AllowTrailingCommas = true });
            if (config is not null)
                return config;
        }

        var bridgeId = Guid.NewGuid().ToString("N");
        var serverUrl = "ws://localhost:5846/bridge/connect";

        var newConfig = new Configuration
        {
            BridgeId = bridgeId,
            ServerUrl = serverUrl,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(newConfig,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);

        return newConfig;
    }
}
