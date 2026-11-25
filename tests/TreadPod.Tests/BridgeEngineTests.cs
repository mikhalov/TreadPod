using TreadPod.Core.Bridge;
using TreadPod.Core.Protocol;
using Xunit;

namespace TreadPod.Tests;

public sealed class BridgeEngineTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class FakeRscServer : IRscPeripheralServer
    {
        public RscStartResult NextStartResult { get; set; } = new(RscStartStatus.Started);
        public RscStartResult? NextRestartResult { get; set; }
        public bool HasSubscribedClients { get; set; }
        public int RestartCount { get; private set; }
        public List<byte[]> Notifications { get; } = [];
        public event Action<string>? DiagnosticMessage;

        public void EmitDiagnostic(string message) => DiagnosticMessage?.Invoke(message);

        public Task<RscStartResult> StartAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextStartResult);

        public Task<RscStartResult> RestartAdvertisingAsync(CancellationToken cancellationToken)
        {
            RestartCount++;
            return Task.FromResult(NextRestartResult ?? NextStartResult);
        }

        public Task NotifyMeasurementAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            Notifications.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeR2Client : IR2Client
    {
        public string DeviceName => "KS-SC-BLR2C";
        public bool IsConnected { get; set; } = true;
        public int AskStatsCount { get; private set; }
        public bool Disposed { get; private set; }

        public event Action<ReadOnlyMemory<byte>>? PacketReceived;
        public event Action? Disconnected;

        public Task SendAskStatsAsync(CancellationToken cancellationToken)
        {
            AskStatsCount++;
            return Task.CompletedTask;
        }

        public void EmitStatus(byte state, double speedKmh, int timeSec, int distDecameters, int steps)
        {
            var pkt = new byte[20];
            pkt[0] = 0xF8;
            pkt[1] = 0xA2;
            pkt[2] = state;
            pkt[3] = (byte)Math.Round(speedKmh * 10);
            pkt[5] = (byte)((timeSec >> 16) & 0xFF);
            pkt[6] = (byte)((timeSec >> 8) & 0xFF);
            pkt[7] = (byte)(timeSec & 0xFF);
            pkt[8] = (byte)((distDecameters >> 16) & 0xFF);
            pkt[9] = (byte)((distDecameters >> 8) & 0xFF);
            pkt[10] = (byte)(distDecameters & 0xFF);
            pkt[11] = (byte)((steps >> 16) & 0xFF);
            pkt[12] = (byte)((steps >> 8) & 0xFF);
            pkt[13] = (byte)(steps & 0xFF);
            pkt[14] = pkt[3];
            pkt[19] = 0xFD;
            PacketReceived?.Invoke(pkt);
        }

        public void SimulateDrop()
        {
            IsConnected = false;
            Disconnected?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeR2Factory : IR2ClientFactory
    {
        private readonly Func<FakeR2Client?> _create;
        public int ScanCalls { get; private set; }

        public FakeR2Factory(Func<FakeR2Client?> create) => _create = create;

        public Task<IR2Client?> ScanAndConnectAsync(CancellationToken cancellationToken)
        {
            ScanCalls++;
            return Task.FromResult<IR2Client?>(_create());
        }
    }

    private sealed class InMemorySessionStore : ISessionStore
    {
        public List<RunSessionRecord> Items { get; } = [];
        public Task<IReadOnlyList<RunSessionRecord>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RunSessionRecord>>(Items.ToList());

        public Task AppendAsync(RunSessionRecord session, CancellationToken cancellationToken)
        {
            Items.Insert(0, session);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StartupHintClearsOnWatchSubscribe_AndConnectsR2()
    {
        var clock = new ManualTimeProvider();
        var rsc = new FakeRscServer();
        var r2 = new FakeR2Client();
        var factory = new FakeR2Factory(() => r2);
        var store = new InMemorySessionStore();

        await using var engine = new BridgeEngine(rsc, factory, store, clock);
        await engine.StartAsync();

        Assert.Equal(BridgePhase.WaitingForWatch, engine.CurrentSnapshot.Phase);
        Assert.True(engine.CurrentSnapshot.NeedsWatchRepairHint);

        rsc.HasSubscribedClients = true;
        await engine.TickAsync();

        Assert.False(engine.CurrentSnapshot.NeedsWatchRepairHint);
        Assert.Equal(1, factory.ScanCalls);
        Assert.Equal(1, r2.AskStatsCount);

        r2.EmitStatus(state: 1, speedKmh: 7.5, timeSec: 10, distDecameters: 2, steps: 20);
        clock.Advance(TimeSpan.FromSeconds(5));
        r2.EmitStatus(state: 1, speedKmh: 7.5, timeSec: 15, distDecameters: 3, steps: 34);
        await engine.TickAsync();

        Assert.Equal(BridgePhase.Running, engine.CurrentSnapshot.Phase);
        Assert.Equal(7.5, engine.CurrentSnapshot.SpeedKmh, 1);
        Assert.True(engine.CurrentSnapshot.CadenceSpm > 0);
        Assert.Equal(RscProtocol.EncodeMeasurement(7.5, engine.CurrentSnapshot.CadenceSpm), rsc.Notifications.Last());
    }

    [Fact]
    public async Task ShortWatchDisconnectUnder30s_KeepsR2Connected_AndOver30sReleasesAndSaves()
    {
        var clock = new ManualTimeProvider();
        var rsc = new FakeRscServer { HasSubscribedClients = true };
        var r2 = new FakeR2Client();
        var factory = new FakeR2Factory(() => r2);
        var store = new InMemorySessionStore();

        await using var engine = new BridgeEngine(rsc, factory, store, clock);
        await engine.StartAsync();
        await engine.TickAsync();

        r2.EmitStatus(state: 1, speedKmh: 8.0, timeSec: 600, distDecameters: 133, steps: 1600);
        await engine.TickAsync();

        // Watch briefly drops for 15 s (pre-start/reconnect behavior)
        rsc.HasSubscribedClients = false;
        clock.Advance(TimeSpan.FromSeconds(15));
        await engine.TickAsync();
        Assert.False(r2.Disposed);

        // Watch absent > 30 s -> workout saved -> release R2
        clock.Advance(TimeSpan.FromSeconds(20));
        await engine.TickAsync();

        Assert.True(r2.Disposed);
        Assert.Single(store.Items);
        Assert.Equal("watch saved", store.Items[0].ReleaseReason);
        Assert.Equal(1.33, store.Items[0].DistanceKm, 2);
    }

    [Fact]
    public async Task BeltIdleOver180s_ReleasesR2_AndBlocksReconnectUntilNextWatchSession()
    {
        var clock = new ManualTimeProvider();
        var rsc = new FakeRscServer { HasSubscribedClients = true };
        var r2 = new FakeR2Client();
        var factory = new FakeR2Factory(() => r2);
        var store = new InMemorySessionStore();

        await using var engine = new BridgeEngine(rsc, factory, store, clock);
        await engine.StartAsync();
        await engine.TickAsync();

        r2.EmitStatus(state: 1, speedKmh: 6.0, timeSec: 120, distDecameters: 20, steps: 300);
        await engine.TickAsync();

        // Belt stops
        clock.Advance(TimeSpan.FromSeconds(1));
        r2.EmitStatus(state: 0, speedKmh: 0.0, timeSec: 120, distDecameters: 20, steps: 300);
        await engine.TickAsync();

        // After 181 s idle -> R2 released for KS Fit even though watch is still open
        clock.Advance(TimeSpan.FromSeconds(181));
        await engine.TickAsync();

        Assert.True(r2.Disposed);
        Assert.Equal(BridgePhase.ReleasedForKsFit, engine.CurrentSnapshot.Phase);
        Assert.Single(store.Items);
        Assert.Equal("3 min idle", store.Items[0].ReleaseReason);

        // Must NOT reconnect during the same watch session
        clock.Advance(TimeSpan.FromSeconds(5));
        await engine.TickAsync();
        Assert.Equal(1, factory.ScanCalls);

        // After watch leaves past grace and returns -> new workout can reconnect
        rsc.HasSubscribedClients = false;
        clock.Advance(TimeSpan.FromSeconds(35));
        await engine.TickAsync();
        rsc.HasSubscribedClients = true;
        await engine.TickAsync();
        Assert.Equal(2, factory.ScanCalls);
    }

    [Fact]
    public async Task StalledStepCounterAndUnknownBeltState_KeepSpeedAndNonZeroCadence()
    {
        var clock = new ManualTimeProvider();
        var rsc = new FakeRscServer { HasSubscribedClients = true };
        var r2 = new FakeR2Client();
        var factory = new FakeR2Factory(() => r2);
        var store = new InMemorySessionStore();

        await using var engine = new BridgeEngine(rsc, factory, store, clock);
        await engine.StartAsync();
        await engine.TickAsync();

        r2.EmitStatus(state: 1, speedKmh: 6.5, timeSec: 10, distDecameters: 2, steps: 100);
        clock.Advance(TimeSpan.FromSeconds(5));
        r2.EmitStatus(state: 1, speedKmh: 6.5, timeSec: 15, distDecameters: 3, steps: 114);
        var validSpm = engine.CurrentSnapshot.CadenceSpm;
        Assert.True(validSpm > 0);

        // Step counter stalls (steps remain 114 for 15 s) -> cadence holds last non-zero value
        clock.Advance(TimeSpan.FromSeconds(15));
        r2.EmitStatus(state: 1, speedKmh: 6.5, timeSec: 30, distDecameters: 6, steps: 114);
        Assert.Equal(validSpm, engine.CurrentSnapshot.CadenceSpm);

        // Unknown belt state 15 -> ignored, keeps 6.5 km/h
        r2.EmitStatus(state: 15, speedKmh: 0.0, timeSec: 31, distDecameters: 6, steps: 114);
        Assert.Equal(6.5, engine.CurrentSnapshot.SpeedKmh, 1);
    }

    [Fact]
    public async Task SystemResume_RestartsAdvertisingAndReEnablesRepairHint()
    {
        var clock = new ManualTimeProvider();
        var rsc = new FakeRscServer { HasSubscribedClients = true };
        var factory = new FakeR2Factory(() => new FakeR2Client());
        var store = new InMemorySessionStore();

        await using var engine = new BridgeEngine(rsc, factory, store, clock);
        await engine.StartAsync();
        await engine.TickAsync();
        Assert.False(engine.CurrentSnapshot.NeedsWatchRepairHint);

        await engine.NotifySystemResumeAsync();

        Assert.Equal(1, rsc.RestartCount);
        Assert.True(engine.CurrentSnapshot.NeedsWatchRepairHint);
        Assert.Equal(BridgePhase.WaitingForWatch, engine.CurrentSnapshot.Phase);
    }

    [Fact]
    public async Task RadioNotAvailable_EntersBluetoothOffAndRecoversAutomatically()
    {
        var clock = new ManualTimeProvider();
        var rsc = new FakeRscServer
        {
            NextStartResult = new RscStartResult(RscStartStatus.BluetoothOff, "Bluetooth is turned off")
        };
        var factory = new FakeR2Factory(() => new FakeR2Client());
        var store = new InMemorySessionStore();

        await using var engine = new BridgeEngine(rsc, factory, store, clock);
        await engine.StartAsync();

        Assert.Equal(BridgePhase.BluetoothOff, engine.CurrentSnapshot.Phase);
        Assert.True(engine.CurrentSnapshot.NeedsWatchRepairHint);

        // User turns Bluetooth back on -> recovers on retry tick
        rsc.NextStartResult = new RscStartResult(RscStartStatus.Started);
        clock.Advance(TimeSpan.FromSeconds(6));
        await engine.TickAsync();

        Assert.Equal(BridgePhase.WaitingForWatch, engine.CurrentSnapshot.Phase);
    }

    [Fact]
    public async Task ServerDiagnosticMessages_AppearInRecentLogs()
    {
        var clock = new ManualTimeProvider();
        var rsc = new FakeRscServer
        {
            NextStartResult = new RscStartResult(RscStartStatus.Started, "status=Started")
        };
        var factory = new FakeR2Factory(() => new FakeR2Client());
        var store = new InMemorySessionStore();

        await using var engine = new BridgeEngine(rsc, factory, store, clock);
        await engine.StartAsync();

        rsc.EmitDiagnostic("Subscribers on 0x2A53: 1 (COROS)");
        var logs = engine.GetRecentLogs();

        Assert.Contains(logs, l => l.Contains("status=Started"));
        Assert.Contains(logs, l => l.Contains("Subscribers on 0x2A53: 1 (COROS)"));
    }

    [Fact]
    public async Task SystemResume_WhenAdvertisingPreserved_DoesNotShowRepairHint()
    {
        var clock = new ManualTimeProvider();
        var rsc = new FakeRscServer
        {
            HasSubscribedClients = true,
            NextRestartResult = new RscStartResult(RscStartStatus.Started, "status=Started (preserved)", AddressPreserved: true)
        };
        var factory = new FakeR2Factory(() => new FakeR2Client());
        var store = new InMemorySessionStore();

        await using var engine = new BridgeEngine(rsc, factory, store, clock);
        await engine.StartAsync();
        await engine.TickAsync();
        Assert.False(engine.CurrentSnapshot.NeedsWatchRepairHint);

        await engine.NotifySystemResumeAsync();

        Assert.Equal(1, rsc.RestartCount);
        Assert.False(engine.CurrentSnapshot.NeedsWatchRepairHint);
        Assert.Contains(engine.GetRecentLogs(), l => l.Contains("same address"));
    }
}
