namespace TreadPod.Core.Bridge;

public enum BridgePhase
{
    Starting,
    WaitingForWatch,
    ScanningR2,
    ConnectedIdle,
    Running,
    ReleasedForKsFit,
    BluetoothOff,
    Error
}

public sealed record BridgeSnapshot(
    BridgePhase Phase,
    bool WatchConnected,
    string? R2Name,
    string R2StatusText,
    double SpeedKmh,
    int CadenceSpm,
    int RscCadence,
    int Steps,
    int BeltTimeSeconds,
    double BeltDistanceKm,
    byte? BeltState,
    bool NeedsWatchRepairHint,
    string? ErrorMessage,
    DateTimeOffset UpdatedAt);

public sealed record RunSessionRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int DurationSeconds,
    double DistanceKm,
    int Steps,
    double AvgSpeedKmh,
    string ReleaseReason);

public enum RscStartStatus
{
    Started,
    BluetoothOff,
    Failed
}

public sealed record RscStartResult(
    RscStartStatus Status,
    string? Details = null,
    bool AddressPreserved = false,
    bool IsRetryable = true);

public interface IRscPeripheralServer : IAsyncDisposable
{
    bool HasSubscribedClients { get; }
    event Action<string>? DiagnosticMessage;
    Task<RscStartResult> StartAsync(CancellationToken cancellationToken);
    Task<RscStartResult> RestartAdvertisingAsync(CancellationToken cancellationToken);
    Task NotifyMeasurementAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

public interface IR2Client : IAsyncDisposable
{
    string DeviceName { get; }
    bool IsConnected { get; }
    event Action<ReadOnlyMemory<byte>>? PacketReceived;
    event Action? Disconnected;
    Task SendAskStatsAsync(CancellationToken cancellationToken);
}

public interface IR2ClientFactory
{
    Task<IR2Client?> ScanAndConnectAsync(CancellationToken cancellationToken);
}

public interface ISessionStore
{
    Task<IReadOnlyList<RunSessionRecord>> LoadAsync(CancellationToken cancellationToken);
    Task AppendAsync(RunSessionRecord session, CancellationToken cancellationToken);
}

public interface IAutostartService
{
    bool IsSupported { get; }
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
