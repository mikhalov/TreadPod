using System.Runtime.InteropServices.WindowsRuntime;
using TreadPod.Core.Bridge;
using TreadPod.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace TreadPod.App.Bluetooth;

public sealed class WinRtR2ClientFactory : IR2ClientFactory
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);

    public async Task<IR2Client?> ScanAndConnectAsync(CancellationToken cancellationToken)
    {
        var found = await FindKingSmithDeviceAsync(ScanTimeout, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var (address, name) = found.Value;
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null)
        {
            return null;
        }

        GattDeviceService? service = null;
        try
        {
            var servicesRes = await device.GetGattServicesForUuidAsync(R2Protocol.ServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesRes.Status != GattCommunicationStatus.Success || servicesRes.Services.Count == 0)
            {
                device.Dispose();
                return null;
            }

            service = servicesRes.Services[0];
            var notifyRes = await service.GetCharacteristicsForUuidAsync(R2Protocol.NotifyUuid, BluetoothCacheMode.Uncached);
            var writeRes = await service.GetCharacteristicsForUuidAsync(R2Protocol.WriteUuid, BluetoothCacheMode.Uncached);

            if (notifyRes.Status != GattCommunicationStatus.Success || notifyRes.Characteristics.Count == 0 ||
                writeRes.Status != GattCommunicationStatus.Success || writeRes.Characteristics.Count == 0)
            {
                service.Dispose();
                device.Dispose();
                return null;
            }

            var notifyChar = notifyRes.Characteristics[0];
            var writeChar = writeRes.Characteristics[0];
            var client = new WinRtR2Client(device, service, notifyChar, writeChar, string.IsNullOrWhiteSpace(name) ? device.Name : name);

            var cccdStatus = await notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (cccdStatus != GattCommunicationStatus.Success)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return client;
        }
        catch
        {
            service?.Dispose();
            device.Dispose();
            throw;
        }
    }

    private static async Task<(ulong Address, string Name)?> FindKingSmithDeviceAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<(ulong Address, string Name)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            var localName = args.Advertisement?.LocalName;
            if (!string.IsNullOrWhiteSpace(localName) &&
                localName.StartsWith("KS", StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult((args.BluetoothAddress, localName));
            }
        };

        watcher.Received += handler;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var reg = timeoutCts.Token.Register(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            else
            {
                tcs.TrySetResult(null);
            }
        });

        try
        {
            watcher.Start();
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                watcher.Stop();
            }
            catch
            {
                // Ignore if watcher never started or already stopped.
            }

            watcher.Received -= handler;
        }
    }
}

internal sealed class WinRtR2Client : IR2Client
{
    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _notifyChar;
    private readonly GattCharacteristic _writeChar;
    private int _disposed;

    public WinRtR2Client(
        BluetoothLEDevice device,
        GattDeviceService service,
        GattCharacteristic notifyChar,
        GattCharacteristic writeChar,
        string deviceName)
    {
        _device = device;
        _service = service;
        _notifyChar = notifyChar;
        _writeChar = writeChar;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "KS-R2" : deviceName;

        _notifyChar.ValueChanged += OnValueChanged;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string DeviceName { get; }

    public bool IsConnected =>
        Volatile.Read(ref _disposed) == 0 &&
        _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
    public event Action? Disconnected;

    public async Task SendAskStatsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var buffer = WinRtRscPeripheralServer.ToBuffer(R2Protocol.AskStatsCommand.Span);
        var status = await _writeChar.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
        if (status != GattCommunicationStatus.Success)
        {
            throw new IOException($"R2 GATT write failed: {status}");
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var bytes = args.CharacteristicValue.ToArray();
        PacketReceived?.Invoke(bytes);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            Disconnected?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _notifyChar.ValueChanged -= OnValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        try
        {
            await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
        catch
        {
            // Device may already be unreachable; continue disposing handles so Windows releases BLE immediately.
        }

        _service.Dispose();
        _device.Dispose();
    }
}
