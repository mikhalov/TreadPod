using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TreadPod.App.Bluetooth;
using TreadPod.App.Localization;
using TreadPod.App.Platform;
using TreadPod.App.ViewModels;
using TreadPod.App.Views;
using TreadPod.Core.Bridge;

namespace TreadPod.App;

public sealed class App : Application
{
    private BridgeEngine? _engine;
    private FileLogSink? _fileLogSink;
    private PowerResumeMonitor? _powerMonitor;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayToggleItem;
    private NativeMenuItem? _trayExitItem;
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

            try
            {
                _fileLogSink = new FileLogSink();
                _fileLogSink.WriteSystem("--- TreadPod started ---");
            }
            catch
            {
                // Ignore if %LocalAppData%\TreadPod\logs is unwritable.
            }

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                _fileLogSink?.WriteSystem($"FATAL UnhandledException: {e.ExceptionObject}");
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                _fileLogSink?.WriteSystem($"UnobservedTaskException: {e.Exception}");
            };

            var autostart = new WindowsAutostartService();
            var settingsStore = new JsonAppSettingsStore();
            var initialLanguage = settingsStore.LoadLanguage() ?? JsonAppSettingsStore.DetectDefaultLanguage();
            var sessionStore = new JsonSessionStore();
            var rscServer = new WinRtRscPeripheralServer();
            var r2Factory = new WinRtR2ClientFactory();

            _engine = new BridgeEngine(rscServer, r2Factory, sessionStore);
            _viewModel = new MainViewModel(autostart, settingsStore, initialLanguage);
            _mainWindow = new MainWindow { DataContext = _viewModel };

            _viewModel.HideRequested += () => _mainWindow?.Hide();
            _viewModel.ExitRequested += () => _ = ShutdownGracefullyAsync();
            _viewModel.LanguageChanged += lang => Dispatcher.UIThread.Post(() => ApplyTrayLanguage(lang));
            _viewModel.CopyLogsRequested += text =>
            {
                if (_mainWindow?.Clipboard is { } clipboard)
                {
                    _ = clipboard.SetTextAsync(text);
                }
            };
            _viewModel.OpenLogsFolderRequested += OpenLogsFolder;
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
                _fileLogSink?.Write(line);
                Dispatcher.UIThread.Post(() => _viewModel?.AddLog(line));
            };
            _engine.SessionsChanged += () =>
            {
                _ = LoadSessionsIntoViewModelAsync(sessionStore);
            };

            SetupTrayIcon(initialLanguage);
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

    private void SetupTrayIcon(AppLanguage language)
    {
        var strings = UiStrings.For(language);
        var menu = new NativeMenu();
        _trayToggleItem = new NativeMenuItem(strings.TrayOpenLabel);
        _trayToggleItem.Click += (_, _) => TogglePopover();
        _trayExitItem = new NativeMenuItem(strings.TrayExitLabel);
        _trayExitItem.Click += (_, _) => _ = ShutdownGracefullyAsync();

        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_trayExitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = strings.TrayTooltip,
            Icon = TrayIconBitmapFactory.Create(BridgePhase.WaitingForWatch),
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => TogglePopover();
    }

    private void ApplyTrayLanguage(AppLanguage language)
    {
        var strings = UiStrings.For(language);
        if (_trayToggleItem is not null)
        {
            _trayToggleItem.Header = strings.TrayOpenLabel;
        }

        if (_trayExitItem is not null)
        {
            _trayExitItem.Header = strings.TrayExitLabel;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = strings.TrayTooltip;
        }
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
            _fileLogSink?.WriteSystem($"Loop error: {ex.Message}");
            Dispatcher.UIThread.Post(() => _viewModel?.AddLog($"Loop error: {ex.Message}"));
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            var dir = _fileLogSink?.LogDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TreadPod",
                "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell open failures.
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
        finally
        {
            _fileLogSink?.WriteSystem("--- TreadPod exiting ---");
            _fileLogSink?.Dispose();
            _fileLogSink = null;
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
