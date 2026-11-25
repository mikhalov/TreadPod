using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace TreadPod.App.Bluetooth;

public static class BleSelfCheck
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                Console.WriteLine("Bluetooth adapter: NOT FOUND");
                return 1;
            }

            var radio = await adapter.GetRadioAsync();
            var radioState = radio?.State.ToString() ?? "Unknown";

            Console.WriteLine($"BluetoothDeviceId: {adapter.DeviceId}");
            Console.WriteLine($"IsLowEnergySupported: {adapter.IsLowEnergySupported}");
            Console.WriteLine($"IsPeripheralRoleSupported: {adapter.IsPeripheralRoleSupported}");
            Console.WriteLine($"RadioState: {radioState}");

            if (!adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
            {
                Console.WriteLine("FAIL: adapter lacks BLE or Peripheral role support.");
                return 1;
            }

            if (radio?.State != RadioState.On)
            {
                Console.WriteLine("WARN: Bluetooth radio is not On (turn on Bluetooth to advertise).");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
