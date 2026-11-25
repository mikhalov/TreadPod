# Foot pod emulator: PC advertises BLE Running Speed and Cadence, speed toggles 8 <-> 10 km/h every 20 s.
# Direct WinRT (no bless: bless 0.3.0 has unresolvable deps on Python 3.12+).
import asyncio, struct, time, uuid
from winrt.windows.devices.bluetooth import BluetoothAdapter
from winrt.windows.devices.bluetooth.genericattributeprofile import (
    GattServiceProvider, GattLocalCharacteristicParameters, GattCharacteristicProperties,
    GattProtectionLevel, GattServiceProviderAdvertisingParameters)
from winrt.windows.storage.streams import DataWriter

SVC  = uuid.UUID("00001814-0000-1000-8000-00805f9b34fb")  # Running Speed and Cadence
MEAS = uuid.UUID("00002a53-0000-1000-8000-00805f9b34fb")  # RSC Measurement (notify)
FEAT = uuid.UUID("00002a54-0000-1000-8000-00805f9b34fb")  # RSC Feature (read)

def measurement(kmh, cadence=85):
    # flags 0x04 = running; speed in 1/256 m/s; cadence in steps/min
    return struct.pack("<BHB", 0x04, round(kmh / 3.6 * 256), cadence)

def buf(data):
    w = DataWriter(); w.write_bytes(data); return w.detach_buffer()

def params(props, value=None):
    p = GattLocalCharacteristicParameters()
    p.characteristic_properties = props
    p.read_protection_level = GattProtectionLevel.PLAIN
    if value is not None:
        p.static_value = buf(value)
    return p

async def main():
    adapter = await BluetoothAdapter.get_default_async()
    print("Peripheral role supported:", adapter.is_peripheral_role_supported)

    res = await GattServiceProvider.create_async(SVC)
    if res.service_provider is None:
        raise SystemExit(f"GATT service not created: {res.error.name} (RADIO_NOT_AVAILABLE = Bluetooth is off)")
    provider = res.service_provider
    meas = (await provider.service.create_characteristic_async(
        MEAS, params(GattCharacteristicProperties.NOTIFY))).characteristic
    await provider.service.create_characteristic_async(
        FEAT, params(GattCharacteristicProperties.READ, b"\x00\x00"))

    adv = GattServiceProviderAdvertisingParameters()
    adv.is_discoverable = True
    adv.is_connectable = True
    provider.start_advertising_with_parameters(adv)
    await asyncio.sleep(1)
    print("Advertising status:", provider.advertisement_status.name, "- Ctrl+C to stop.")

    while True:
        kmh = 8 if (int(time.time()) // 20) % 2 == 0 else 10
        await meas.notify_value_async(buf(measurement(kmh)))
        print(f"\rsending {kmh} km/h, subscribers: {len(meas.subscribed_clients)} ", end="")
        await asyncio.sleep(1)

if __name__ == "__main__":
    # 8 km/h = 2.222 m/s * 256 = 569 = 0x0239; 10 km/h = 711 = 0x02C7; cadence 85 = 0x55
    assert measurement(8) == b"\x04\x39\x02\x55" and measurement(10) == b"\x04\xc7\x02\x55"
    asyncio.run(main())
