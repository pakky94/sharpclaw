using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace SharpClaw.BridgeClient;

public class TrayIconManager : IDisposable
{
    private readonly BridgeClientService _service;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _connectItem;
    private NativeMenuItem? _disconnectItem;

    public TrayIconManager(BridgeClientService service)
    {
        _service = service;
        CreateTrayIcon();
    }

    private void CreateTrayIcon()
    {
        _connectItem = new NativeMenuItem("Connect") { IsEnabled = true };
        _disconnectItem = new NativeMenuItem("Disconnect") { IsEnabled = false };

        var openConfigItem = new NativeMenuItem("Open Config");
        var quitItem = new NativeMenuItem("Quit");

        _connectItem.Click += (_, _) => _ = _service.ConnectAsync();
        _disconnectItem.Click += (_, _) => _service.Disconnect();
        openConfigItem.Click += (_, _) => OpenConfigFile();
        quitItem.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Add(_connectItem);
        menu.Add(_disconnectItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(openConfigItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "SharpClaw Bridge — Disconnected",
            Menu = menu,
            Icon = LoadIcon(),
        };

        _trayIcon.IsVisible = true;
    }

    public void UpdateStatus(BridgeStatus status)
    {
        var text = status switch
        {
            BridgeStatus.Disconnected => "SharpClaw Bridge — Disconnected",
            BridgeStatus.Connecting => "SharpClaw Bridge — Connecting...",
            BridgeStatus.Connected => "SharpClaw Bridge — Connected",
            BridgeStatus.Reconnecting => "SharpClaw Bridge — Reconnecting...",
            _ => "SharpClaw Bridge",
        };

        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;

        if (_connectItem is not null)
            _connectItem.IsEnabled = status is BridgeStatus.Disconnected;

        if (_disconnectItem is not null)
            _disconnectItem.IsEnabled = status is BridgeStatus.Connected;
    }

    private static void OpenConfigFile()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "sharpclaw", "config.json");

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = OperatingSystem.IsWindows()
                ? "notepad.exe"
                : OperatingSystem.IsMacOS()
                    ? "open"
                    : "xdg-open";
            process.StartInfo.Arguments = OperatingSystem.IsWindows() ? configPath : $"\"{configPath}\"";
            process.StartInfo.UseShellExecute = true;
            process.Start();
        }
        catch
        {
            // Fallback: try with shell execute
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = configPath;
                process.StartInfo.UseShellExecute = true;
                process.Start();
            }
            catch { }
        }
    }

    private static void Quit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static WindowIcon LoadIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("SharpClaw.BridgeClient.favicon.ico")
            ?? throw new InvalidOperationException("Embedded resource 'favicon.ico' not found.");
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
