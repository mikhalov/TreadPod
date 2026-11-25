namespace TreadPod.App.Localization;

public sealed class UiStrings
{
    public required string PinTooltip { get; init; }
    public required string MenuTooltip { get; init; }
    public required string HideTooltip { get; init; }

    public required string TabStatus { get; init; }
    public required string TabHistory { get; init; }
    public required string TabLog { get; init; }

    public required string ToastRepairHint { get; init; }
    public required string ToastBtOffFallback { get; init; }

    public required string WatchError { get; init; }
    public required string WatchConnected { get; init; }
    public required string WatchWaiting { get; init; }

    public required string R2Scanning { get; init; }
    public required string R2Paused { get; init; }
    public required string R2Connected { get; init; }
    public required string R2Running { get; init; }
    public required string R2Released { get; init; }
    public required string R2BtOff { get; init; }
    public required string R2Error { get; init; }
    public required string R2Idle { get; init; }

    public required string PipeScanning { get; init; }
    public required string PipeReady { get; init; }
    public required string PipeLive { get; init; }
    public required string PipeKsFit { get; init; }
    public required string PipeBtOff { get; init; }
    public required string PipeError { get; init; }
    public required string PipeWaiting { get; init; }

    public required string CurrentPaceLabel { get; init; }
    public required string LowSpeedPaceNote { get; init; }
    public required string CadenceLabel { get; init; }
    public required string StepsLabel { get; init; }
    public required string TimeDistanceLabel { get; init; }

    public required string PaceUnit { get; init; }
    public required string SpeedUnit { get; init; }
    public required string CadenceUnit { get; init; }
    public required string DistanceUnit { get; init; }
    public required string MinUnit { get; init; }
    public required string TodayLabel { get; init; }
    public required string DateCultureName { get; init; }

    public required string IdleScanningTitle { get; init; }
    public required string IdleScanningDescription { get; init; }
    public required string IdleConnectedTitle { get; init; }
    public required string IdleConnectedDescription { get; init; }
    public required string IdleReleasedTitle { get; init; }
    public required string IdleReleasedDescription { get; init; }
    public required string IdleBtOffTitle { get; init; }
    public required string IdleBtOffDescription { get; init; }
    public required string IdleErrorTitle { get; init; }
    public required string IdleErrorDescription { get; init; }
    public required string IdleWaitingTitle { get; init; }
    public required string IdleWaitingDescription { get; init; }

    public required string HistoryEmpty { get; init; }
    public required string LogEmpty { get; init; }

    public required string AutostartLabel { get; init; }
    public required string LanguageLabel { get; init; }
    public required string ExitMenuLabel { get; init; }
    public required string ExitConfirmMessage { get; init; }
    public required string ExitCancelLabel { get; init; }
    public required string ExitConfirmLabel { get; init; }

    public required string TrayOpenLabel { get; init; }
    public required string TrayExitLabel { get; init; }
    public required string TrayTooltip { get; init; }

    public required string ReasonWatchSaved { get; init; }
    public required string ReasonBeltIdle { get; init; }
    public required string ReasonR2Disconnected { get; init; }
    public required string ReasonPcSleep { get; init; }
    public required string ReasonExit { get; init; }

    public string LocalizeReleaseReason(string reason) => reason switch
    {
        "watch saved" or "годинник зберіг" => ReasonWatchSaved,
        "3 min idle" or "простій 3 хв" => ReasonBeltIdle,
        "R2 disconnected" or "R2 відключилась" => ReasonR2Disconnected,
        "PC sleep" or "сон ПК" => ReasonPcSleep,
        "exit" or "вихід" => ReasonExit,
        _ => reason
    };

    public static UiStrings For(AppLanguage language) => language == AppLanguage.Uk ? Uk : En;

    public static UiStrings En { get; } = new()
    {
        PinTooltip = "Pin always on top",
        MenuTooltip = "Settings",
        HideTooltip = "Hide to tray",

        TabStatus = "Status",
        TabHistory = "History",
        TabLog = "Log",

        ToastRepairHint = "New BLE address — re-pair pod on your watch",
        ToastBtOffFallback = "PC Bluetooth is turned off (RADIO_NOT_AVAILABLE)",

        WatchError = "error",
        WatchConnected = "connected",
        WatchWaiting = "waiting",

        R2Scanning = "scanning…",
        R2Paused = "paused",
        R2Connected = "connected",
        R2Running = "running (1)",
        R2Released = "released",
        R2BtOff = "off",
        R2Error = "error",
        R2Idle = "idle",

        PipeScanning = "scanning",
        PipeReady = "ready",
        PipeLive = "1 Hz",
        PipeKsFit = "KS Fit",
        PipeBtOff = "BT OFF",
        PipeError = "error",
        PipeWaiting = "standby",

        CurrentPaceLabel = "Current pace",
        LowSpeedPaceNote = "watch shows pace from 2.5 km/h",
        CadenceLabel = "Cadence",
        StepsLabel = "Steps",
        TimeDistanceLabel = "Time / Dist",

        PaceUnit = "/km",
        SpeedUnit = "km/h",
        CadenceUnit = "spm",
        DistanceUnit = "km",
        MinUnit = "min",
        TodayLabel = "Today",
        DateCultureName = "en-US",

        IdleScanningTitle = "Watch ready — scanning R2",
        IdleScanningDescription = "Power on KingSmith R2 (make sure KS Fit on your phone is closed).",
        IdleConnectedTitle = "Treadmill connected",
        IdleConnectedDescription = "Belt is stationary. Once you start running, pace will stream to COROS every second.",
        IdleReleasedTitle = "Treadmill released for KS Fit",
        IdleReleasedDescription = "You can sync the session in KS Fit now. Next workout on your watch starts automatically.",
        IdleBtOffTitle = "PC Bluetooth is off",
        IdleBtOffDescription = "Turn on Bluetooth in Windows so the bridge can resume automatically.",
        IdleErrorTitle = "Bluetooth adapter error",
        IdleErrorDescription = "Check your Bluetooth adapter in Device Manager.",
        IdleWaitingTitle = "Bridge active in tray",
        IdleWaitingDescription = "Start Indoor Run on COROS — the rest connects automatically.",

        HistoryEmpty = "Completed runs will appear here (duration, R2 distance, and release reason).",
        LogEmpty = "No protocol events yet.",

        AutostartLabel = "Start with Windows",
        LanguageLabel = "Language",
        ExitMenuLabel = "Quit TreadPod",
        ExitConfirmMessage = "Quitting rotates the BLE sensor address — you will need to re-pair it on your watch. Quit anyway?",
        ExitCancelLabel = "Cancel",
        ExitConfirmLabel = "Quit",

        TrayOpenLabel = "Open TreadPod",
        TrayExitLabel = "Quit (resets BLE address)",
        TrayTooltip = "TreadPod — KingSmith R2 → COROS bridge",

        ReasonWatchSaved = "watch saved",
        ReasonBeltIdle = "3 min idle",
        ReasonR2Disconnected = "R2 disconnected",
        ReasonPcSleep = "PC sleep",
        ReasonExit = "exit"
    };

    public static UiStrings Uk { get; } = new()
    {
        PinTooltip = "Закріпити поверх вікон",
        MenuTooltip = "Параметри",
        HideTooltip = "Згорнути в трей",

        TabStatus = "Стан",
        TabHistory = "Історія",
        TabLog = "Лог",

        ToastRepairHint = "Нова адреса — додай датчик на годиннику заново",
        ToastBtOffFallback = "Bluetooth на ПК вимкнено (RADIO_NOT_AVAILABLE)",

        WatchError = "помилка",
        WatchConnected = "підключено",
        WatchWaiting = "очікує",

        R2Scanning = "пошук…",
        R2Paused = "пауза",
        R2Connected = "підключено",
        R2Running = "біг (1)",
        R2Released = "відпущено",
        R2BtOff = "вимкнено",
        R2Error = "помилка",
        R2Idle = "вільна",

        PipeScanning = "пошук",
        PipeReady = "готові",
        PipeLive = "1 Гц",
        PipeKsFit = "KS Fit",
        PipeBtOff = "BT OFF",
        PipeError = "помилка",
        PipeWaiting = "готовий",

        CurrentPaceLabel = "Темп зараз",
        LowSpeedPaceNote = "на годиннику від 2.5 км/год",
        CadenceLabel = "Каденс",
        StepsLabel = "Кроки",
        TimeDistanceLabel = "Час / Дис.",

        PaceUnit = "/км",
        SpeedUnit = "км/год",
        CadenceUnit = "кр/хв",
        DistanceUnit = "км",
        MinUnit = "хв",
        TodayLabel = "Сьогодні",
        DateCultureName = "uk-UA",

        IdleScanningTitle = "Годинник готовий — шукаю R2",
        IdleScanningDescription = "Увімкни доріжку KingSmith R2 (KS Fit на телефоні має бути закритий).",
        IdleConnectedTitle = "Доріжку підключено",
        IdleConnectedDescription = "Полотно зараз стоїть. Коли почнеш біг, темп піде на COROS щосекунди.",
        IdleReleasedTitle = "Доріжку відпущено для KS Fit",
        IdleReleasedDescription = "Можеш забрати сесію в KS Fit. Наступне тренування на годиннику стартує автоматично.",
        IdleBtOffTitle = "Bluetooth на ПК вимкнено",
        IdleBtOffDescription = "Увімкни Bluetooth у Windows, щоб міст відновив роботу.",
        IdleErrorTitle = "Помилка Bluetooth-адаптера",
        IdleErrorDescription = "Перевір Bluetooth-адаптер у Диспетчері пристроїв.",
        IdleWaitingTitle = "Міст активний у фоні",
        IdleWaitingDescription = "Почни Indoor Run на COROS — далі все підхопиться автоматично.",

        HistoryEmpty = "Тут зʼявляться завершені пробіжки (час, дистанція R2 та причина відключення).",
        LogEmpty = "Подій протоколу ще немає.",

        AutostartLabel = "Автозапуск з Windows",
        LanguageLabel = "Мова",
        ExitMenuLabel = "Вийти з TreadPod",
        ExitConfirmMessage = "Перезапуск змінить BLE-адресу датчика — годинник доведеться додавати заново. Точно вийти?",
        ExitCancelLabel = "Ні",
        ExitConfirmLabel = "Вийти",

        TrayOpenLabel = "Відкрити TreadPod",
        TrayExitLabel = "Вийти (змінить BLE-адресу)",
        TrayTooltip = "TreadPod — міст KingSmith R2 → COROS",

        ReasonWatchSaved = "годинник зберіг",
        ReasonBeltIdle = "простій 3 хв",
        ReasonR2Disconnected = "R2 відключилась",
        ReasonPcSleep = "сон ПК",
        ReasonExit = "вихід"
    };
}
