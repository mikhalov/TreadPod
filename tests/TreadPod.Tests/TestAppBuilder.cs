using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(TreadPod.Tests.TestAppBuilder))]

namespace TreadPod.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TreadPod.App.App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            });
}
