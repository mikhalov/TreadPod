using TreadPod.Core.Bridge;
using TreadPod.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace TreadPod.App.Bluetooth;

public sealed class WinRtRscPeripheralServer : IRscPeripheralServer
{
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _measurementChar;

    public event Action<string>? DiagnosticMessage;

    public bool HasSubscribedClients
    {
        get
        {
            try
            {
                return _measurementChar?.SubscribedClients.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<RscStartResult> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            return new RscStartResult(RscStartStatus.BluetoothOff, "Bluetooth adapter not found");
        }

        if (!adapter.IsPeripheralRoleSupported)
        {
            return new RscStartResult(RscStartStatus.Failed, "Bluetooth adapter does not support BLE peripheral role");
        }

        var result = await GattServiceProvider.CreateAsync(RscProtocol.ServiceUuid);
        if (result.Error == BluetoothError.RadioNotAvailable)
        {
            return new RscStartResult(RscStartStatus.BluetoothOff, "Bluetooth is turned off (RADIO_NOT_AVAILABLE)");
        }

        if (result.ServiceProvider is null || result.Error != BluetoothError.Success)
        {
            return new RscStartResult(RscStartStatus.Failed, $"Failed to create GATT server: {result.Error}");
        }

        var provider = result.ServiceProvider;

        var measParams = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.Plain
        };
        var measRes = await provider.Service.CreateCharacteristicAsync(RscProtocol.MeasurementUuid, measParams);
        if (measRes.Error != BluetoothError.Success || measRes.Characteristic is null)
        {
            return new RscStartResult(RscStartStatus.Failed, $"Failed to create characteristic 0x2A53: {measRes.Error}");
        }

        var featParams = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = GattProtectionLevel.Plain,
            StaticValue = ToBuffer(RscProtocol.FeatureBytes.Span)
        };
        var featRes = await provider.Service.CreateCharacteristicAsync(RscProtocol.FeatureUuid, featParams);
        if (featRes.Error != BluetoothError.Success)
        {
            return new RscStartResult(RscStartStatus.Failed, $"Failed to create characteristic 0x2A54: {featRes.Error}");
        }

        var adv = new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true
        };

        _provider = provider;
        _measurementChar = measRes.Characteristic;
        _measurementChar.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _provider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;

        var startedTcs = new TaskCompletionSource<GattServiceProviderAdvertisementStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnInitStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
        {
            if (args.Status is GattServiceProviderAdvertisementStatus.Started
                or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
            {
                startedTcs.TrySetResult(args.Status);
            }
        }

        provider.AdvertisementStatusChanged += OnInitStatusChanged;
        try
        {
            provider.StartAdvertising(adv);
            if (provider.AdvertisementStatus is GattServiceProviderAdvertisementStatus.Started
                or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
            {
                startedTcs.TrySetResult(provider.AdvertisementStatus);
            }

            await Task.WhenAny(startedTcs.Task, Task.Delay(2000, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            provider.AdvertisementStatusChanged -= OnInitStatusChanged;
        }

        var advStatus = provider.AdvertisementStatus;
        if (advStatus is GattServiceProviderAdvertisementStatus.Aborted
            or GattServiceProviderAdvertisementStatus.Stopped)
        {
            await DisposeAsync().ConfigureAwait(false);
            return new RscStartResult(RscStartStatus.Failed, $"BLE advertising stopped: {advStatus}");
        }

        return new RscStartResult(RscStartStatus.Started, $"status={advStatus}");
    }

    public async Task<RscStartResult> RestartAdvertisingAsync(CancellationToken cancellationToken)
    {
        var provider = _provider;
        if (provider is not null)
        {
            try
            {
                var status = provider.AdvertisementStatus;
                if (status is GattServiceProviderAdvertisementStatus.Started
                    or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
                {
                    return new RscStartResult(
                        RscStartStatus.Started,
                        $"status={status} (preserved)",
                        AddressPreserved: true);
                }
            }
            catch
            {
                // Provider invalidated during sleep; recreate cleanly below.
            }
        }

        await DisposeAsync().ConfigureAwait(false);
        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyMeasurementAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var meas = _measurementChar;
        if (meas is null)
        {
            return;
        }

        if (meas.SubscribedClients.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await meas.NotifyValueAsync(ToBuffer(payload.Span));
    }

    public ValueTask DisposeAsync()
    {
        var provider = _provider;
        var measurementChar = _measurementChar;
        _provider = null;
        _measurementChar = null;

        if (measurementChar is not null)
        {
            try
            {
                measurementChar.SubscribedClientsChanged -= OnSubscribedClientsChanged;
            }
            catch
            {
            }
        }

        if (provider is not null)
        {
            try
            {
                provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
                if (provider.AdvertisementStatus is GattServiceProviderAdvertisementStatus.Started
                    or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
                {
                    provider.StopAdvertising();
                }
            }
            catch
            {
                // Best-effort cleanup on shutdown/sleep.
            }
        }

        return ValueTask.CompletedTask;
    }

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        try
        {
            var clients = sender.SubscribedClients;
            if (clients.Count == 0)
            {
                DiagnosticMessage?.Invoke("Subscribers on 0x2A53: 0 (client unsubscribed)");
                return;
            }

            var ids = string.Join(", ", clients.Select(c => c.Session?.DeviceId?.Id ?? "?"));
            DiagnosticMessage?.Invoke($"Subscribers on 0x2A53: {clients.Count} ({ids})");
        }
        catch (Exception ex)
        {
            DiagnosticMessage?.Invoke($"SubscribedClientsChanged err: {ex.Message}");
        }
    }

    private void OnAdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        var err = args.Error == BluetoothError.Success ? string.Empty : $", error={args.Error}";
        DiagnosticMessage?.Invoke($"BLE AdvertisementStatus -> {args.Status}{err}");
    }

    internal static IBuffer ToBuffer(ReadOnlySpan<byte> bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes.ToArray());
        return writer.DetachBuffer();
    }
}
