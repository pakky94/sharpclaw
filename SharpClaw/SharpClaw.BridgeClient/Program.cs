using Avalonia;
using SharpClaw.BridgeClient;

namespace SharpClaw.BridgeClient;

public static class Program
{
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            //.WithInterFont()
            .LogToTrace();
    }
}
