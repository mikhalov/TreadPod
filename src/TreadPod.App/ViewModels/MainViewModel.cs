using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using TreadPod.Core.Bridge;
using TreadPod.Core.Protocol;

namespace TreadPod.App.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record SessionItemViewModel(
    string Title,
    string Subtitle,
    string DistanceText,
    string AvgPaceText);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const double SparkWidth = 280.0;
    private const double SparkHeight = 40.0;
    private const int MaxSparkPoints = 20;

    private static readonly IBrush DotEmerald = new SolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush DotAmber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush DotSky = new SolidColorBrush(Color.Parse("#38BDF8"));
    private static readonly IBrush DotRose = new SolidColorBrush(Color.Parse("#F43F5E"));
    private static readonly IBrush DotSlate = new SolidColorBrush(Color.Parse("#94A3B8"));

    private static readonly IBrush ToastInfoBg = new SolidColorBrush(Color.Parse("#1E0EA5E9"));
    private static readonly IBrush ToastInfoBorder = new SolidColorBrush(Color.Parse("#4D38BDF8"));
    private static readonly IBrush ToastInfoText = new SolidColorBrush(Color.Parse("#7DD3FC"));

    private static readonly IBrush ToastErrBg = new SolidColorBrush(Color.Parse("#24F43F5E"));
    private static readonly IBrush ToastErrBorder = new SolidColorBrush(Color.Parse("#59F43F5E"));
    private static readonly IBrush ToastErrText = new SolidColorBrush(Color.Parse("#FDA4AF"));

    private static readonly IBrush ToggleOnBg = new SolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush ToggleOffBg = new SolidColorBrush(Color.Parse("#475569"));

    private readonly IAutostartService? _autostart;
    private readonly List<double> _speedHistory = [];

    private BridgePhase _currentPhase = BridgePhase.WaitingForWatch;
    private string _selectedTab = "status";
    private bool _isPinned;
    private bool _isMenuOpen;
    private bool _isExitConfirmOpen;
    private bool _isAutostartEnabled;
    private bool _hintDismissed;
    private bool _needsWatchRepairHint = true;
    private string? _errorMessage;

    private string _r2ShortStatus = "вільна";
    private string _watchShortStatus = "очікує";
    private string _pipeLabel = "готовий";
    private string _paceText = "—";
    private string _speedText = "0.0 км/год";
    private string _cadenceText = "0 кр/хв";
    private string _stepsText = "0";
    private string _timeDistanceText = "00:00 · 0.00";
    private string _idleTitle = "Міст активний у фоні";
    private string _idleDescription = "Почни Indoor Run на COROS — далі все підхопиться автоматично.";
    private bool _showLowSpeedPaceNote;
    private IList<Point> _sparklinePoints = [];
    private IList<Point> _sparklineFillPoints = [];

    public MainViewModel(IAutostartService? autostart = null)
    {
        _autostart = autostart;
        _isAutostartEnabled = autostart?.IsEnabled() ?? false;

        SelectStatusTabCommand = new RelayCommand(() => SelectedTab = "status");
        SelectHistoryTabCommand = new RelayCommand(() => SelectedTab = "history");
        SelectLogTabCommand = new RelayCommand(() => SelectedTab = "log");
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);
        ToggleMenuCommand = new RelayCommand(() =>
        {
            IsMenuOpen = !IsMenuOpen;
            if (!IsMenuOpen)
            {
                IsExitConfirmOpen = false;
            }
        });
        CloseMenuCommand = new RelayCommand(() =>
        {
            IsMenuOpen = false;
            IsExitConfirmOpen = false;
        });
        ToggleAutostartCommand = new RelayCommand(ToggleAutostart);
        DismissToastCommand = new RelayCommand(() =>
        {
            _hintDismissed = true;
            NotifyToastProperties();
        });
        RequestExitCommand = new RelayCommand(() => IsExitConfirmOpen = true);
        CancelExitCommand = new RelayCommand(() => IsExitConfirmOpen = false);
        ConfirmExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        HideWindowCommand = new RelayCommand(() =>
        {
            IsMenuOpen = false;
            IsExitConfirmOpen = false;
            HideRequested?.Invoke();
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? HideRequested;
    public event Action? ExitRequested;
    public event Action<BridgePhase>? StatusVisualChanged;

    public ICommand SelectStatusTabCommand { get; }
    public ICommand SelectHistoryTabCommand { get; }
    public ICommand SelectLogTabCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand ToggleMenuCommand { get; }
    public ICommand CloseMenuCommand { get; }
    public ICommand ToggleAutostartCommand { get; }
    public ICommand DismissToastCommand { get; }
    public ICommand RequestExitCommand { get; }
    public ICommand CancelExitCommand { get; }
    public ICommand ConfirmExitCommand { get; }
    public ICommand HideWindowCommand { get; }

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];

    public string SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetField(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsStatusTab));
                OnPropertyChanged(nameof(IsHistoryTab));
                OnPropertyChanged(nameof(IsLogTab));
            }
        }
    }

    public bool IsStatusTab => _selectedTab == "status";
    public bool IsHistoryTab => _selectedTab == "history";
    public bool IsLogTab => _selectedTab == "log";

    public bool IsPinned
    {
        get => _isPinned;
        set => SetField(ref _isPinned, value);
    }

    public bool IsMenuOpen
    {
        get => _isMenuOpen;
        set => SetField(ref _isMenuOpen, value);
    }

    public bool IsExitConfirmOpen
    {
        get => _isExitConfirmOpen;
        set => SetField(ref _isExitConfirmOpen, value);
    }

    public bool IsAutostartEnabled
    {
        get => _isAutostartEnabled;
        set
        {
            if (SetField(ref _isAutostartEnabled, value))
            {
                OnPropertyChanged(nameof(AutostartBrush));
                OnPropertyChanged(nameof(AutostartKnobMargin));
            }
        }
    }

    public IBrush AutostartBrush => _isAutostartEnabled ? ToggleOnBg : ToggleOffBg;
    public Thickness AutostartKnobMargin => _isAutostartEnabled ? new Thickness(16, 0, 0, 0) : new Thickness(0);

    public bool IsRunning => _currentPhase == BridgePhase.Running;
    public bool IsIdle => !IsRunning;

    public bool IsToastVisible =>
        _currentPhase is BridgePhase.BluetoothOff or BridgePhase.Error ||
        (_needsWatchRepairHint && !_hintDismissed);

    public bool CanDismissToast => _currentPhase is not (BridgePhase.BluetoothOff or BridgePhase.Error);

    public string ToastText => _currentPhase switch
    {
        BridgePhase.BluetoothOff or BridgePhase.Error => _errorMessage ?? "Bluetooth на ПК вимкнено (RADIO_NOT_AVAILABLE)",
        _ => "Нова адреса — додай датчик на годиннику заново"
    };

    public IBrush ToastBackgroundBrush => CanDismissToast ? ToastInfoBg : ToastErrBg;
    public IBrush ToastBorderBrush => CanDismissToast ? ToastInfoBorder : ToastErrBorder;
    public IBrush ToastTextBrush => CanDismissToast ? ToastInfoText : ToastErrText;

    public IBrush StatusDotBrush => _currentPhase switch
    {
        BridgePhase.Running => DotEmerald,
        BridgePhase.ScanningR2 or BridgePhase.ConnectedIdle => DotAmber,
        BridgePhase.ReleasedForKsFit => DotSky,
        BridgePhase.BluetoothOff or BridgePhase.Error => DotRose,
        _ => DotSlate
    };

    public string R2ShortStatus
    {
        get => _r2ShortStatus;
        private set => SetField(ref _r2ShortStatus, value);
    }

    public string WatchShortStatus
    {
        get => _watchShortStatus;
        private set => SetField(ref _watchShortStatus, value);
    }

    public string PipeLabel
    {
        get => _pipeLabel;
        private set => SetField(ref _pipeLabel, value);
    }

    public string PaceText
    {
        get => _paceText;
        private set => SetField(ref _paceText, value);
    }

    public bool ShowLowSpeedPaceNote
    {
        get => _showLowSpeedPaceNote;
        private set => SetField(ref _showLowSpeedPaceNote, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetField(ref _speedText, value);
    }

    public string CadenceText
    {
        get => _cadenceText;
        private set => SetField(ref _cadenceText, value);
    }

    public string StepsText
    {
        get => _stepsText;
        private set => SetField(ref _stepsText, value);
    }

    public string TimeDistanceText
    {
        get => _timeDistanceText;
        private set => SetField(ref _timeDistanceText, value);
    }

    public string IdleTitle
    {
        get => _idleTitle;
        private set => SetField(ref _idleTitle, value);
    }

    public string IdleDescription
    {
        get => _idleDescription;
        private set => SetField(ref _idleDescription, value);
    }

    public IList<Point> SparklinePoints
    {
        get => _sparklinePoints;
        private set => SetField(ref _sparklinePoints, value);
    }

    public IList<Point> SparklineFillPoints
    {
        get => _sparklineFillPoints;
        private set => SetField(ref _sparklineFillPoints, value);
    }

    public bool HasSessions => Sessions.Count > 0;
    public bool HasNoSessions => Sessions.Count == 0;
    public bool HasLogs => LogEntries.Count > 0;
    public bool HasNoLogs => LogEntries.Count == 0;

    public void ApplySnapshot(BridgeSnapshot snapshot, IReadOnlyList<double>? seedSpeedHistory = null)
    {
        var previousPhase = _currentPhase;
        var previousHint = _needsWatchRepairHint;

        _currentPhase = snapshot.Phase;
        _errorMessage = snapshot.ErrorMessage;

        if (!previousHint && snapshot.NeedsWatchRepairHint)
        {
            _hintDismissed = false;
        }

        _needsWatchRepairHint = snapshot.NeedsWatchRepairHint;

        WatchShortStatus = snapshot.Phase switch
        {
            BridgePhase.BluetoothOff or BridgePhase.Error => "помилка",
            _ => snapshot.WatchConnected ? "підключено" : "очікує"
        };

        R2ShortStatus = snapshot.Phase switch
        {
            BridgePhase.ScanningR2 => "пошук…",
            BridgePhase.ConnectedIdle => snapshot.SpeedKmh == 0 && snapshot.Steps > 0 ? "пауза" : "підключено",
            BridgePhase.Running => "біг (1)",
            BridgePhase.ReleasedForKsFit => "відпущено",
            BridgePhase.BluetoothOff => "вимкнено",
            BridgePhase.Error => "помилка",
            _ => "вільна"
        };

        PipeLabel = snapshot.Phase switch
        {
            BridgePhase.ScanningR2 => "пошук",
            BridgePhase.ConnectedIdle => "готові",
            BridgePhase.Running => "1 Гц",
            BridgePhase.ReleasedForKsFit => "KS Fit",
            BridgePhase.BluetoothOff => "BT OFF",
            BridgePhase.Error => "помилка",
            _ => "готовий"
        };

        (IdleTitle, IdleDescription) = snapshot.Phase switch
        {
            BridgePhase.ScanningR2 => (
                "Годинник готовий — шукаю R2",
                "Увімкни доріжку KingSmith R2 (KS Fit на телефоні має бути закритий)."),
            BridgePhase.ConnectedIdle => (
                "Доріжку підключено",
                "Полотно зараз стоїть. Коли почнеш біг, темп піде на COROS щосекунди."),
            BridgePhase.ReleasedForKsFit => (
                "Доріжку відпущено для KS Fit",
                "Можеш забрати сесію в KS Fit. Наступне тренування на годиннику стартує автоматично."),
            BridgePhase.BluetoothOff => (
                "Bluetooth на ПК вимкнено",
                snapshot.ErrorMessage ?? "Увімкни Bluetooth у Windows, щоб міст відновив роботу."),
            BridgePhase.Error => (
                "Помилка Bluetooth-адаптера",
                snapshot.ErrorMessage ?? "Перевір Bluetooth-адаптер у Диспетчері пристроїв."),
            _ => (
                "Міст активний у фоні",
                "Почни Indoor Run на COROS — далі все підхопиться автоматично.")
        };

        PaceText = snapshot.Phase == BridgePhase.Running
            ? RscProtocol.FormatPace(snapshot.SpeedKmh)
            : "—";
        ShowLowSpeedPaceNote = snapshot.Phase == BridgePhase.Running && snapshot.SpeedKmh < 2.5;
        SpeedText = string.Create(CultureInfo.InvariantCulture, $"{snapshot.SpeedKmh:0.0} км/год");
        CadenceText = $"{snapshot.CadenceSpm} кр/хв";
        StepsText = FormatThousands(snapshot.Steps);

        var mm = snapshot.BeltTimeSeconds / 60;
        var ss = snapshot.BeltTimeSeconds % 60;
        TimeDistanceText = string.Create(CultureInfo.InvariantCulture, $"{mm:00}:{ss:00} · {snapshot.BeltDistanceKm:0.00}");

        if (seedSpeedHistory is { Count: > 0 })
        {
            _speedHistory.Clear();
            foreach (var v in seedSpeedHistory.TakeLast(MaxSparkPoints))
            {
                _speedHistory.Add(v);
            }
        }
        else if (snapshot.Phase == BridgePhase.Running)
        {
            _speedHistory.Add(snapshot.SpeedKmh);
            if (_speedHistory.Count > MaxSparkPoints)
            {
                _speedHistory.RemoveAt(0);
            }
        }
        else if (snapshot.Phase is BridgePhase.WaitingForWatch or BridgePhase.ReleasedForKsFit)
        {
            _speedHistory.Clear();
        }

        RebuildSparkline();

        if (previousPhase != _currentPhase)
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(StatusDotBrush));
            StatusVisualChanged?.Invoke(_currentPhase);
        }

        NotifyToastProperties();
    }

    public void SetSessions(IReadOnlyList<RunSessionRecord> records)
    {
        Sessions.Clear();
        var culture = CultureInfo.GetCultureInfo("uk-UA");
        foreach (var r in records)
        {
            var localStart = r.StartedAt.ToLocalTime();
            var datePart = localStart.Date == DateTime.Today
                ? "Сьогодні"
                : localStart.ToString("d MMM", culture);
            var title = $"{datePart} · {localStart:HH:mm}";
            var mins = Math.Max(1, (int)Math.Round(r.DurationSeconds / 60.0));
            var subtitle = $"{mins} хв · {r.ReleaseReason}";
            var distance = string.Create(CultureInfo.InvariantCulture, $"{r.DistanceKm:0.00} км");
            var pace = RscProtocol.FormatPace(r.AvgSpeedKmh);
            Sessions.Add(new SessionItemViewModel(title, subtitle, distance, pace));
        }

        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasNoSessions));
    }

    public void SetLogs(IReadOnlyList<string> lines)
    {
        LogEntries.Clear();
        foreach (var line in lines)
        {
            LogEntries.Add(line);
        }

        OnPropertyChanged(nameof(HasLogs));
        OnPropertyChanged(nameof(HasNoLogs));
    }

    public void AddLog(string line)
    {
        LogEntries.Add(line);
        while (LogEntries.Count > 100)
        {
            LogEntries.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasLogs));
        OnPropertyChanged(nameof(HasNoLogs));
    }

    private void ToggleAutostart()
    {
        var next = !IsAutostartEnabled;
        if (_autostart is { IsSupported: true })
        {
            try
            {
                _autostart.SetEnabled(next);
                IsAutostartEnabled = _autostart.IsEnabled();
                return;
            }
            catch
            {
                // Keep UI state unchanged if registry write fails.
                return;
            }
        }

        IsAutostartEnabled = next;
    }

    private void RebuildSparkline()
    {
        if (_speedHistory.Count < 2)
        {
            SparklinePoints = [];
            SparklineFillPoints = [];
            return;
        }

        var rawMin = _speedHistory.Min();
        var rawMax = _speedHistory.Max();
        var min = Math.Max(0.0, rawMin - 0.6);
        var max = Math.Max(rawMax + 0.4, min + 1.5);
        const double padY = 4.0;
        var span = max - min;

        var line = new List<Point>(_speedHistory.Count);
        for (var i = 0; i < _speedHistory.Count; i++)
        {
            var x = i / (double)(_speedHistory.Count - 1) * SparkWidth;
            var norm = (_speedHistory[i] - min) / span;
            var y = SparkHeight - padY - norm * (SparkHeight - padY * 2.0);
            line.Add(new Point(x, y));
        }

        var fill = new List<Point>(line.Count + 2);
        fill.AddRange(line);
        fill.Add(new Point(SparkWidth, SparkHeight));
        fill.Add(new Point(0, SparkHeight));

        SparklinePoints = line;
        SparklineFillPoints = fill;
    }

    private void NotifyToastProperties()
    {
        OnPropertyChanged(nameof(IsToastVisible));
        OnPropertyChanged(nameof(CanDismissToast));
        OnPropertyChanged(nameof(ToastText));
        OnPropertyChanged(nameof(ToastBackgroundBrush));
        OnPropertyChanged(nameof(ToastBorderBrush));
        OnPropertyChanged(nameof(ToastTextBrush));
    }

    private static string FormatThousands(int value)
    {
        var nfi = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        nfi.NumberGroupSeparator = " ";
        return value.ToString("N0", nfi);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
