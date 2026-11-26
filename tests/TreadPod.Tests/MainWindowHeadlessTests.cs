using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using TreadPod.App.Localization;
using TreadPod.App.ViewModels;
using TreadPod.App.Views;
using TreadPod.Core.Bridge;
using Xunit;

namespace TreadPod.Tests;

public sealed class MainWindowHeadlessTests
{
    private static readonly string ScreenshotDir =
        Environment.GetEnvironmentVariable("TREADPOD_SCREENSHOT_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "TreadPod.TestScreenshots");

    [AvaloniaFact]
    public void ClickThroughAllControls_AndCaptureRenderedScreenshots()
    {
        Directory.CreateDirectory(ScreenshotDir);

        var vm = new MainViewModel(initialLanguage: AppLanguage.En);
        var exitFired = false;
        string? copiedLogText = null;
        var openLogsFired = false;
        vm.ExitRequested += () => exitFired = true;
        vm.CopyLogsRequested += text => copiedLogText = text;
        vm.OpenLogsFolderRequested += () => openLogsFired = true;

        var window = new MainWindow
        {
            DataContext = vm
        };
        window.Show();
        FlushUi();

        // 1. Startup state with repair hint banner
        vm.ApplySnapshot(new BridgeSnapshot(
            Phase: BridgePhase.WaitingForWatch,
            WatchConnected: false,
            R2Name: null,
            R2StatusText: "Idle",
            SpeedKmh: 0,
            CadenceSpm: 0,
            RscCadence: 0,
            Steps: 0,
            BeltTimeSeconds: 0,
            BeltDistanceKm: 0,
            BeltState: null,
            NeedsWatchRepairHint: true,
            ErrorMessage: null,
            UpdatedAt: DateTimeOffset.Now));
        FlushUi();
        Assert.Equal(430, window.Bounds.Height);
        SaveFrame(window, "01_startup.png");

        var toastBanner = window.FindControl<Border>("ToastBanner")!;
        var dismissToastButton = window.FindControl<Button>("DismissToastButton")!;
        Assert.True(toastBanner.IsVisible);

        // Click real Dismiss button on startup hint
        ClickControl(window, dismissToastButton);
        Assert.False(toastBanner.IsVisible);
        Assert.Equal(430, window.Bounds.Height);

        // 2. Active running state at 7.5 km/h with sparkline
        vm.ApplySnapshot(
            new BridgeSnapshot(
                Phase: BridgePhase.Running,
                WatchConnected: true,
                R2Name: "KS-SC-BLR2C",
                R2StatusText: "Streaming",
                SpeedKmh: 7.5,
                CadenceSpm: 162,
                RscCadence: 81,
                Steps: 1420,
                BeltTimeSeconds: 872,
                BeltDistanceKm: 1.82,
                BeltState: 1,
                NeedsWatchRepairHint: false,
                ErrorMessage: null,
                UpdatedAt: DateTimeOffset.Now),
            seedSpeedHistory: [6.4, 6.8, 7.0, 7.2, 7.2, 7.5, 7.5, 7.4, 7.5, 7.6, 7.5, 7.5]);
        FlushUi();
        Assert.Equal("8:00 /km", window.FindControl<TextBlock>("PaceReadoutText")!.Text);
        Assert.Equal(430, window.Bounds.Height);
        SaveFrame(window, "02_running.png");

        // 3. Populate history & click History tab
        vm.SetSessions(
        [
            new RunSessionRecord(DateTimeOffset.Now.AddHours(-2), DateTimeOffset.Now.AddMinutes(-78), 2520, 5.86, 6140, 8.4, "watch saved"),
            new RunSessionRecord(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddDays(-2).AddMinutes(31), 1860, 4.20, 4390, 8.1, "3 min idle"),
            new RunSessionRecord(DateTimeOffset.Now.AddDays(-4), DateTimeOffset.Now.AddDays(-4).AddMinutes(50), 3000, 7.10, 7420, 8.5, "watch saved")
        ]);
        vm.SetLogs(
        [
            "20:32:10 R2 FE01 state=1 7.5 km/h steps=1418",
            "20:32:11 RSC 0x2A53 notify 04 15 02 51 (7.5 km/h, 162 spm)",
            "20:32:12 R2 FE01 state=1 7.5 km/h steps=1420"
        ]);
        FlushUi();

        ClickControl(window, window.FindControl<Button>("TabHistoryButton")!);
        Assert.True(window.FindControl<Control>("HistoryTabPanel")!.IsVisible);
        Assert.False(window.FindControl<Control>("StatusTabPanel")!.IsVisible);
        Assert.Equal(430, window.Bounds.Height);
        SaveFrame(window, "03_history.png");

        // Click Log tab, capture frame, and verify Copy + Logs folder actions
        ClickControl(window, window.FindControl<Button>("TabLogButton")!);
        Assert.True(window.FindControl<Control>("LogTabPanel")!.IsVisible);
        Assert.Equal(430, window.Bounds.Height);
        SaveFrame(window, "05_log_tab.png");

        var copyLogButton = window.FindControl<Button>("CopyLogButton")!;
        ClickControl(window, copyLogButton);
        Assert.NotNull(copiedLogText);
        Assert.Contains("RSC 0x2A53 notify", copiedLogText);
        Assert.Equal("Copied", copyLogButton.Content);

        ClickControl(window, window.FindControl<Button>("OpenLogsFolderButton")!);
        Assert.True(openLogsFired);

        // Return to Status tab, test Pin toggle
        ClickControl(window, window.FindControl<Button>("TabStatusButton")!);
        Assert.False(window.Topmost);
        ClickControl(window, window.FindControl<Button>("PinButton")!);
        Assert.True(window.Topmost);

        // 4. Open overlay menu -> toggle Autostart -> click Exit -> verify inline warning -> cancel -> click backdrop
        ClickControl(window, window.FindControl<Button>("MenuButton")!);
        var menuOverlay = window.FindControl<Control>("MenuOverlay")!;
        Assert.True(menuOverlay.IsVisible);
        Assert.Equal(430, window.Bounds.Height);

        var initialAutostart = vm.IsAutostartEnabled;
        ClickControl(window, window.FindControl<Button>("AutostartButton")!);
        Assert.NotEqual(initialAutostart, vm.IsAutostartEnabled);

        ClickControl(window, window.FindControl<Button>("ExitTriggerButton")!);
        var exitConfirmBox = window.FindControl<Border>("ExitConfirmBox")!;
        Assert.True(exitConfirmBox.IsVisible);
        Assert.Equal(430, window.Bounds.Height);
        SaveFrame(window, "04_menu_exit_confirm.png");

        ClickControl(window, window.FindControl<Button>("ExitCancelButton")!);
        Assert.False(exitConfirmBox.IsVisible);
        Assert.False(exitFired);

        // Toggle language to Ukrainian and back to English inside open menu
        ClickControl(window, window.FindControl<Button>("LangUkButton")!);
        Assert.True(vm.IsUkrainian);
        Assert.Equal("8:00 /км", window.FindControl<TextBlock>("PaceReadoutText")!.Text);
        Assert.Equal("Історія", window.FindControl<Button>("TabHistoryButton")!.Content);

        ClickControl(window, window.FindControl<Button>("LangEnButton")!);
        Assert.True(vm.IsEnglish);
        Assert.Equal("8:00 /km", window.FindControl<TextBlock>("PaceReadoutText")!.Text);
        Assert.Equal("History", window.FindControl<Button>("TabHistoryButton")!.Content);

        // Click bottom area of dimmed backdrop -> closes overlay menu
        var backdrop = window.FindControl<Control>("MenuBackdrop")!;
        var backdropPt = backdrop.TranslatePoint(new Point(backdrop.Bounds.Width / 2.0, backdrop.Bounds.Height - 24.0), window);
        Assert.NotNull(backdropPt);
        window.MouseDown(backdropPt!.Value, MouseButton.Left);
        window.MouseUp(backdropPt.Value, MouseButton.Left);
        FlushUi();
        Assert.False(menuOverlay.IsVisible);

        window.AllowProcessClose = true;
        window.Close();
    }

    private static void ClickControl(Window window, Control control)
    {
        FlushUi();
        var center = new Point(control.Bounds.Width / 2.0, control.Bounds.Height / 2.0);
        var pt = control.TranslatePoint(center, window);
        Assert.NotNull(pt);

        window.MouseDown(pt!.Value, MouseButton.Left);
        window.MouseUp(pt.Value, MouseButton.Left);
        FlushUi();
    }

    private static void FlushUi()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveFrame(Window window, string fileName)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var fullPath = Path.Combine(ScreenshotDir, fileName);
        frame!.Save(fullPath);
    }

    [Fact]
    public void UiStrings_LocalizesBothEnglishAndLegacyUkrainianReleaseReasons()
    {
        Assert.Equal("watch saved", UiStrings.En.LocalizeReleaseReason("watch saved"));
        Assert.Equal("watch saved", UiStrings.En.LocalizeReleaseReason("годинник зберіг"));
        Assert.Equal("3 min idle", UiStrings.En.LocalizeReleaseReason("простій 3 хв"));

        Assert.Equal("годинник зберіг", UiStrings.Uk.LocalizeReleaseReason("watch saved"));
        Assert.Equal("простій 3 хв", UiStrings.Uk.LocalizeReleaseReason("3 min idle"));
        Assert.Equal("R2 відключилась", UiStrings.Uk.LocalizeReleaseReason("R2 disconnected"));
    }
}
