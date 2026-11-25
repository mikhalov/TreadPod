using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TreadPod.App.Bluetooth;
using TreadPod.App.Platform;
using TreadPod.App.ViewModels;
using TreadPod.App.Views;
using TreadPod.Core.Bridge;

namespace TreadPod.App;

public sealed class App : Application
{
    private BridgeEngine? _engine;
    private PowerResumeMonitor? _powerMonitor;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var autostart = new WindowsAutostartService();
            var sessionStore = new JsonSessionStore();
            var rscServer = new WinRtRscPeripheralServer();
            var r2Factory = new WinRtR2ClientFactory();

            _engine = new BridgeEngine(rscServer, r2Factory, sessionStore);
            _viewModel = new MainViewModel(autostart);
            _mainWindow = new MainWindow { DataContext = _viewModel };

            _viewModel.HideRequested += () => _mainWindow?.Hide();
            _viewModel.ExitRequested += () => _ = ShutdownGracefullyAsync();
            _viewModel.StatusVisualChanged += phase =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_trayIcon is not null)
                    {
                        _trayIcon.Icon = TrayIconBitmapFactory.Create(phase);
                    }
                });
            };

            _engine.SnapshotChanged += snapshot =>
            {
                Dispatcher.UIThread.Post(() => _viewModel?.ApplySnapshot(snapshot));
            };
            _engine.LogAdded += line =>
            {
                Dispatcher.UIThread.Post(() => _viewModel?.AddLog(line));
            };
            _engine.SessionsChanged += () =>
            {
                _ = LoadSessionsIntoViewModelAsync(sessionStore);
            };

            SetupTrayIcon();
            _powerMonitor = new PowerResumeMonitor(() =>
            {
                if (_engine is not null && _loopCts is not null && !_loopCts.IsCancellationRequested)
                {
                    _ = _engine.NotifySystemResumeAsync(_loopCts.Token);
                }
            });

            _loopCts = new CancellationTokenSource();
            _ = LoadSessionsIntoViewModelAsync(sessionStore);
            _loopTask = RunBridgeLoopAsync(_loopCts.Token);

            // Show briefly positioned above tray on first launch so user sees the new BLE sensor prompt.
            ShowPopover();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon()
    {
        var menu = new NativeMenu();
        var toggleItem = new NativeMenuItem("Відкрити TreadPod");
        toggleItem.Click += (_, _) => TogglePopover();
        var exitItem = new NativeMenuItem("Вийти (змінить BLE-адресу)");
        exitItem.Click += (_, _) => _ = ShutdownGracefullyAsync();

        menu.Items.Add(toggleItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "TreadPod — міст KingSmith R2 → COROS",
            Icon = TrayIconBitmapFactory.Create(BridgePhase.WaitingForWatch),
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => TogglePopover();
    }

    private void TogglePopover()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.IsVisible)
        {
            _mainWindow.Hide();
        }
        else
        {
            ShowPopover();
        }
    }

    private void ShowPopover()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.PositionNearTaskbar();
        _mainWindow.Activate();
    }

    private async Task LoadSessionsIntoViewModelAsync(ISessionStore store)
    {
        try
        {
            var items = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => _viewModel?.SetSessions(items));
        }
        catch
        {
            // Ignore transient read issues.
        }
    }

    private async Task RunBridgeLoopAsync(CancellationToken cancellationToken)
    {
        if (_engine is null)
        {
            return;
        }

        try
        {
            await _engine.StartAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _engine.TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _viewModel?.AddLog($"Loop error: {ex.Message}"));
        }
    }

    private async Task ShutdownGracefullyAsync()
    {
        try
        {
            _powerMonitor?.Dispose();
            _loopCts?.Cancel();
            if (_loopTask is not null)
            {
                await _loopTask.ConfigureAwait(false);
            }

            if (_engine is not null)
            {
                await _engine.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Continue shutting down UI cleanly.
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            if (_mainWindow is not null)
            {
                _mainWindow.AllowProcessClose = true;
                _mainWindow.Close();
            }

            _desktop?.Shutdown();
        });
    }
}
