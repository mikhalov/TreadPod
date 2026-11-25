using Avalonia;
using TreadPod.App.Bluetooth;
using TreadPod.App.Platform;
using Velopack;

namespace TreadPod.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                try
                {
                    new WindowsAutostartService().SetEnabled(false);
                }
                catch
                {
                    // Best-effort registry cleanup during uninstall.
                }
            })
            .Run();

        if (args.Any(a => string.Equals(a, "--check-ble", StringComparison.OrdinalIgnoreCase)))
        {
            return BleSelfCheck.RunAsync().GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
