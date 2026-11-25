using TreadPod.Core.Protocol;

namespace TreadPod.Core.Bridge;

public sealed class BridgeEngine : IAsyncDisposable
{
    public static readonly TimeSpan WatchGracePeriod = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BeltIdleReleasePeriod = TimeSpan.FromSeconds(180);
    public const int DefaultFallbackSpm = 100;
    private const int MaxLogEntries = 100;

    private readonly IRscPeripheralServer _rscServer;
    private readonly IR2ClientFactory _r2Factory;
    private readonly ISessionStore _sessionStore;
    private readonly TimeProvider _timeProvider;
    private readonly CadenceCalculator _cadence = new();
    private readonly object _sync = new();
    private readonly Queue<string> _logs = new();

    private bool _rscStarted;
    private bool _canRetryStart;
    private bool _needsWatchRepairHint = true;
    private bool _releasedForThisSession;
    private DateTimeOffset? _lastWatchSeen;
    private DateTimeOffset? _idleSince;
    private DateTimeOffset? _lastBluetoothRetry;

    private IR2Client? _r2Client;
    private Task<IR2Client?>? _scanTask;
    private CancellationTokenSource? _scanCts;
    private string _r2StatusText = "Idle";
    private string? _r2Name;
    private string? _errorMessage;

    private bool _ranInCurrentConnection;
    private DateTimeOffset? _runStartedAt;
    private double _currentSpeedKmh;
    private int _currentCadenceSpm;
    private int _lastNonZeroCadenceSpm;
    private int _currentSteps;
    private int _beltTimeSeconds;
    private double _beltDistanceKm;
    private byte? _beltState;
    private BridgePhase _phase = BridgePhase.Starting;
    private int _tickCount;

    public BridgeEngine(
        IRscPeripheralServer rscServer,
        IR2ClientFactory r2Factory,
        ISessionStore sessionStore,
        TimeProvider? timeProvider = null)
    {
        _rscServer = rscServer;
        _r2Factory = r2Factory;
        _sessionStore = sessionStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rscServer.DiagnosticMessage += OnRscDiagnostic;
    }

    public event Action<BridgeSnapshot>? SnapshotChanged;
    public event Action<string>? LogAdded;
    public event Action? SessionsChanged;

    public BridgeSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return BuildSnapshotLocked(_timeProvider.GetUtcNow());
            }
        }
    }

    public IReadOnlyList<string> GetRecentLogs()
    {
        lock (_sync)
        {
            return _logs.ToArray();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var result = await _rscServer.StartAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            var previousError = _errorMessage;
            if (result.Status == RscStartStatus.Started)
            {
                _rscStarted = true;
                _canRetryStart = false;
                _errorMessage = null;
                _needsWatchRepairHint = true;
                _phase = BridgePhase.WaitingForWatch;
                _r2StatusText = "Idle";
                var detailsSuffix = string.IsNullOrWhiteSpace(result.Details) ? string.Empty : $" ({result.Details})";
                AppendLogLocked($"RSC 0x1814 advertising{detailsSuffix}; waiting for COROS watch");
            }
            else if (result.Status == RscStartStatus.BluetoothOff)
            {
                _rscStarted = false;
                _canRetryStart = true;
                _phase = BridgePhase.BluetoothOff;
                _r2StatusText = "BT off";
                _errorMessage = result.Details ?? "Bluetooth is turned off (RADIO_NOT_AVAILABLE)";
                _lastBluetoothRetry = _timeProvider.GetUtcNow();
                if (!string.Equals(previousError, _errorMessage, StringComparison.Ordinal))
                {
                    AppendLogLocked($"ERROR: {_errorMessage}");
                }
            }
            else
            {
                _rscStarted = false;
                _canRetryStart = result.IsRetryable;
                _phase = BridgePhase.Error;
                _r2StatusText = "Error";
                _errorMessage = result.Details ?? "Failed to start Bluetooth foot pod";
                _lastBluetoothRetry = _timeProvider.GetUtcNow();
                if (!string.Equals(previousError, _errorMessage, StringComparison.Ordinal))
                {
                    AppendLogLocked($"ERROR: {_errorMessage}");
                }
            }
        }

        PublishSnapshot();
    }

    public async Task NotifySystemResumeAsync(CancellationToken cancellationToken = default)
    {
        await ReleaseR2Async("PC sleep", markSessionBlocked: false, cancellationToken).ConfigureAwait(false);

        var result = _rscStarted
            ? await _rscServer.RestartAdvertisingAsync(cancellationToken).ConfigureAwait(false)
            : await _rscServer.StartAsync(cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            var preserved = result.Status == RscStartStatus.Started && result.AddressPreserved;
            if (!preserved)
            {
                _needsWatchRepairHint = true;
            }
            _releasedForThisSession = false;
            _lastWatchSeen = null;

            if (result.Status == RscStartStatus.Started)
            {
                _rscStarted = true;
                _canRetryStart = false;
                _errorMessage = null;
                _phase = BridgePhase.WaitingForWatch;
                _r2StatusText = "Idle";
                AppendLogLocked(preserved
                    ? "System resumed — BLE advertising preserved (same address)"
                    : "System resumed — BLE advertising restarted");
            }
            else if (result.Status == RscStartStatus.BluetoothOff)
            {
                _rscStarted = false;
                _canRetryStart = true;
                _phase = BridgePhase.BluetoothOff;
                _r2StatusText = "BT off";
                _errorMessage = result.Details ?? "Bluetooth is turned off (RADIO_NOT_AVAILABLE)";
                _lastBluetoothRetry = _timeProvider.GetUtcNow();
                AppendLogLocked($"ERROR: {_errorMessage}");
            }
            else
            {
                _rscStarted = false;
                _canRetryStart = result.IsRetryable;
                _phase = BridgePhase.Error;
                _r2StatusText = "Error";
                _errorMessage = result.Details ?? "Failed to restart BLE advertising after sleep";
                _lastBluetoothRetry = _timeProvider.GetUtcNow();
                AppendLogLocked($"ERROR: {_errorMessage}");
            }
        }

        PublishSnapshot();
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        if (!_rscStarted)
        {
            var shouldRetry = false;
            lock (_sync)
            {
                if (_canRetryStart &&
                    _phase is BridgePhase.BluetoothOff or BridgePhase.Error &&
                    (_lastBluetoothRetry is null || now - _lastBluetoothRetry.Value >= TimeSpan.FromSeconds(5)))
                {
                    _lastBluetoothRetry = now;
                    shouldRetry = true;
                }
            }

            if (shouldRetry)
            {
                await StartAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                PublishSnapshot();
            }
            return;
        }

        var subscribed = _rscServer.HasSubscribedClients;
        bool watchPresent;
        IR2Client? activeClient;
        bool shouldStartScan = false;
        bool shouldReleaseForWatchLeft = false;
        bool shouldHarvestScan = false;

        lock (_sync)
        {
            _tickCount++;
            if (subscribed)
            {
                _lastWatchSeen = now;
                if (_needsWatchRepairHint)
                {
                    _needsWatchRepairHint = false;
                    AppendLogLocked("COROS connected — repair hint dismissed");
                }
            }

            watchPresent = _lastWatchSeen is { } seen && now - seen < WatchGracePeriod;

            if (!watchPresent)
            {
                _releasedForThisSession = false;
                if (_r2Client != null || _scanTask != null)
                {
                    shouldReleaseForWatchLeft = true;
                }
                else if (_phase != BridgePhase.ReleasedForKsFit)
                {
                    _phase = BridgePhase.WaitingForWatch;
                    _r2StatusText = "Idle";
                }
            }
            else if (_scanTask is { IsCompleted: true })
            {
                shouldHarvestScan = true;
            }
            else if (subscribed && _r2Client is null && _scanTask is null && !_releasedForThisSession)
            {
                shouldStartScan = true;
            }

            activeClient = _r2Client;
        }

        if (shouldReleaseForWatchLeft)
        {
            CancelOngoingScan();
            if (activeClient != null)
            {
                await ReleaseR2Async("watch saved", markSessionBlocked: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                lock (_sync)
                {
                    _phase = BridgePhase.WaitingForWatch;
                    _r2StatusText = "Idle";
                }
            }
        }
        else
        {
            if (shouldHarvestScan)
            {
                await CompleteScanTaskAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (shouldStartScan)
            {
                StartScan(cancellationToken);
                if (_scanTask is { IsCompleted: true })
                {
                    await CompleteScanTaskAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await PollConnectedR2Async(now, cancellationToken).ConfigureAwait(false);
        }

        byte[] payload;
        lock (_sync)
        {
            payload = RscProtocol.EncodeMeasurement(_currentSpeedKmh, _currentCadenceSpm);
            if (_tickCount % 5 == 0 || _currentSpeedKmh > 0)
            {
                if (_phase == BridgePhase.Running)
                {
                    AppendLogLocked($"RSC 0x2A53 notify {RscProtocol.FormatHex(payload)} ({_currentSpeedKmh:0.0} km/h, {_currentCadenceSpm} spm)");
                }
            }
        }

        try
        {
            await _rscServer.NotifyMeasurementAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync)
            {
                AppendLogLocked($"RSC notify err: {ex.Message}");
            }
        }

        PublishSnapshot();
    }

    private void StartScan(CancellationToken outerToken)
    {
        lock (_sync)
        {
            if (_scanTask != null || _r2Client != null)
            {
                return;
            }

            _scanCts?.Dispose();
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            _phase = BridgePhase.ScanningR2;
            _r2StatusText = "Scanning KS-*...";
            AppendLogLocked("Watch active — scanning for KingSmith R2...");
            var token = _scanCts.Token;
            _scanTask = _r2Factory.ScanAndConnectAsync(token);
        }
    }

    private void CancelOngoingScan()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _scanCts;
            _scanCts = null;
            _scanTask = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private async Task CompleteScanTaskAsync(CancellationToken cancellationToken)
    {
        Task<IR2Client?>? task;
        lock (_sync)
        {
            task = _scanTask;
            _scanTask = null;
        }

        if (task is null)
        {
            return;
        }

        IR2Client? client = null;
        try
        {
            client = await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                AppendLogLocked($"R2 scan error: {ex.Message}");
                _r2StatusText = "Not found (retrying...)";
            }
            return;
        }

        if (client is null)
        {
            lock (_sync)
            {
                _r2StatusText = "Not found (KS Fit or powered off?)";
                AppendLogLocked("R2 not found — will retry");
            }
            return;
        }

        bool watchStillPresent;
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            watchStillPresent = _lastWatchSeen is { } seen && now - seen < WatchGracePeriod;
            if (!watchStillPresent)
            {
                // Watch left while scanning; release immediately.
            }
            else
            {
                _r2Client = client;
                _r2Name = client.DeviceName;
                _r2StatusText = $"Connected ({client.DeviceName})";
                _phase = BridgePhase.ConnectedIdle;
                _cadence.Reset();
                _ranInCurrentConnection = false;
                _runStartedAt = null;
                _idleSince = null;
                _currentSpeedKmh = 0;
                _currentCadenceSpm = 0;
                _lastNonZeroCadenceSpm = 0;
                _currentSteps = 0;
                _beltTimeSeconds = 0;
                _beltDistanceKm = 0;
                _beltState = null;
                client.PacketReceived += OnR2PacketReceived;
                client.Disconnected += OnR2Disconnected;
                AppendLogLocked($"Connected to {client.DeviceName} (read-only FE00)");
            }
        }

        if (!watchStillPresent)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return;
        }
    }

    private async Task PollConnectedR2Async(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IR2Client? client;
        lock (_sync)
        {
            client = _r2Client;
        }

        if (client is null)
        {
            return;
        }

        if (!client.IsConnected)
        {
            await ReleaseR2Async("R2 disconnected", markSessionBlocked: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await client.SendAskStatsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync)
            {
                AppendLogLocked($"R2 poll failed: {ex.Message}");
            }
            await ReleaseR2Async("R2 disconnected", markSessionBlocked: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var shouldReleaseForIdle = false;
        lock (_sync)
        {
            if (_currentSpeedKmh > 0)
            {
                _ranInCurrentConnection = true;
                _runStartedAt ??= now;
                _idleSince = null;
                _phase = BridgePhase.Running;
                _r2StatusText = "Streaming";
            }
            else if (_ranInCurrentConnection)
            {
                _idleSince ??= now;
                _phase = BridgePhase.ConnectedIdle;
                _r2StatusText = "Belt paused";
                if (now - _idleSince.Value > BeltIdleReleasePeriod)
                {
                    shouldReleaseForIdle = true;
                }
            }
        }

        if (shouldReleaseForIdle)
        {
            await ReleaseR2Async("3 min idle", markSessionBlocked: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnR2PacketReceived(ReadOnlyMemory<byte> packet)
    {
        if (!R2Protocol.TryParseStatus(packet.Span, out var status))
        {
            return;
        }

        if (!R2Protocol.IsKnownBeltState(status.BeltState))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            var running = status.IsRunning;
            var elapsedSec = now.ToUnixTimeMilliseconds() / 1000.0;
            var spm = running ? _cadence.Update(elapsedSec, status.Steps) : 0;
            if (running && spm == 0)
            {
                spm = _lastNonZeroCadenceSpm > 0 ? _lastNonZeroCadenceSpm : DefaultFallbackSpm;
            }
            if (running && spm > 0)
            {
                _lastNonZeroCadenceSpm = spm;
            }

            _beltState = status.BeltState;
            _currentSteps = status.Steps;
            _beltTimeSeconds = status.TimeSeconds;
            _beltDistanceKm = status.DistanceKm;
            _currentSpeedKmh = running ? status.SpeedKmh : 0.0;
            _currentCadenceSpm = running ? spm : 0;

            if (running)
            {
                _ranInCurrentConnection = true;
                _runStartedAt ??= now;
                _idleSince = null;
                _phase = BridgePhase.Running;
                _r2StatusText = "Streaming";
            }
            else if (_r2Client != null)
            {
                _phase = BridgePhase.ConnectedIdle;
                _r2StatusText = _ranInCurrentConnection ? "Belt paused" : $"Connected ({_r2Name})";
            }
        }
    }

    private void OnR2Disconnected()
    {
        lock (_sync)
        {
            AppendLogLocked("R2 connection dropped");
        }
    }

    private async Task ReleaseR2Async(string reason, bool markSessionBlocked, CancellationToken cancellationToken)
    {
        IR2Client? client;
        RunSessionRecord? sessionToSave = null;
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            client = _r2Client;
            _r2Client = null;
            if (client != null)
            {
                client.PacketReceived -= OnR2PacketReceived;
                client.Disconnected -= OnR2Disconnected;
            }

            if (_ranInCurrentConnection)
            {
                var started = _runStartedAt ?? now;
                var duration = Math.Max(_beltTimeSeconds, Math.Max(1, (int)(now - started).TotalSeconds));
                var avgSpeed = duration > 0 ? _beltDistanceKm / (duration / 3600.0) : 0.0;
                sessionToSave = new RunSessionRecord(
                    StartedAt: started,
                    EndedAt: now,
                    DurationSeconds: duration,
                    DistanceKm: Math.Round(_beltDistanceKm, 2),
                    Steps: _currentSteps,
                    AvgSpeedKmh: Math.Round(avgSpeed, 1),
                    ReleaseReason: reason);
            }

            _currentSpeedKmh = 0;
            _currentCadenceSpm = 0;
            _lastNonZeroCadenceSpm = 0;
            _beltState = null;
            _idleSince = null;
            _ranInCurrentConnection = false;
            _runStartedAt = null;
            _releasedForThisSession = markSessionBlocked;
            _r2Name = null;

            if (markSessionBlocked)
            {
                _phase = BridgePhase.ReleasedForKsFit;
                _r2StatusText = "Released · KS Fit free";
            }
            else
            {
                _phase = _rscStarted ? BridgePhase.WaitingForWatch : _phase;
                _r2StatusText = reason == "watch saved" ? "Released · KS Fit free" : "Idle";
            }

            if (client != null)
            {
                AppendLogLocked($"Treadmill released ({reason})");
            }
        }

        if (client != null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    AppendLogLocked($"Dispose R2 err: {ex.Message}");
                }
            }
        }

        if (sessionToSave != null)
        {
            try
            {
                await _sessionStore.AppendAsync(sessionToSave, cancellationToken).ConfigureAwait(false);
                SessionsChanged?.Invoke();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_sync)
                {
                    AppendLogLocked($"Failed to save session: {ex.Message}");
                }
            }
        }
    }

    private BridgeSnapshot BuildSnapshotLocked(DateTimeOffset now)
    {
        var watchConnected = _rscStarted && _rscServer.HasSubscribedClients;
        return new BridgeSnapshot(
            Phase: _phase,
            WatchConnected: watchConnected,
            R2Name: _r2Name,
            R2StatusText: _r2StatusText,
            SpeedKmh: _currentSpeedKmh,
            CadenceSpm: _currentCadenceSpm,
            RscCadence: RscProtocol.ToRscCadence(_currentCadenceSpm),
            Steps: _currentSteps,
            BeltTimeSeconds: _beltTimeSeconds,
            BeltDistanceKm: _beltDistanceKm,
            BeltState: _beltState,
            NeedsWatchRepairHint: _needsWatchRepairHint,
            ErrorMessage: _errorMessage,
            UpdatedAt: now);
    }

    private void AppendLogLocked(string message)
    {
        var timestamp = _timeProvider.GetLocalNow().ToString("HH:mm:ss");
        var line = $"{timestamp} {message}";
        _logs.Enqueue(line);
        while (_logs.Count > MaxLogEntries)
        {
            _logs.Dequeue();
        }

        LogAdded?.Invoke(line);
    }

    private void PublishSnapshot()
    {
        SnapshotChanged?.Invoke(CurrentSnapshot);
    }

    private void OnRscDiagnostic(string message)
    {
        lock (_sync)
        {
            AppendLogLocked(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _rscServer.DiagnosticMessage -= OnRscDiagnostic;
        CancelOngoingScan();
        await ReleaseR2Async("exit", markSessionBlocked: false, CancellationToken.None).ConfigureAwait(false);
        await _rscServer.DisposeAsync().ConfigureAwait(false);
    }
}
