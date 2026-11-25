# KingSmith R2 (KS-SC-BLR2C) read-only probe. This model speaks the WalkingPad protocol:
# service FE00, notify FE01, write FE02 (qdomyos-zwift kingsmithr1protreadmill.cpp handles "KS-SC-BLR2C").
# Only sends the status request f7 a2 00 00 a2 fd (QZ's noOp / ph4-walkingpad ask_stats). Never start/speed.
# Run with KS Fit NOT connected.
import asyncio, sys, time
from bleak import BleakScanner, BleakClient

NOTIFY = "0000fe01-0000-1000-8000-00805f9b34fb"
WRITE  = "0000fe02-0000-1000-8000-00805f9b34fb"
ASK_STATS = bytes([0xF7, 0xA2, 0x00, 0x00, 0xA2, 0xFD])

def parse(p):
    # 20-byte status: [2]=belt state, [3]=speed*10 km/h, [5:8]=time s, [8:11]=distance*10 m?, [11:14]=steps, [14]=target speed*10
    if len(p) != 20:
        return None
    u24 = lambda i: int.from_bytes(p[i:i+3], "big")
    return {"state": p[2], "kmh": p[3] / 10, "time": u24(5), "dist": u24(8), "steps": u24(11), "target": p[14] / 10}

async def main():
    print("Scanning 10 s for KS-* devices...")
    devs = [d for d in await BleakScanner.discover(10) if (d.name or "").upper().startswith("KS")]
    if not devs:
        sys.exit("No KingSmith found. Is the treadmill on and KS Fit disconnected?")
    dev = devs[0]
    print("Connecting to", dev.address, dev.name)

    async with BleakClient(dev) as c:
        last = [None]
        def on_data(_, data):
            st = parse(data)
            line = data.hex(" ") + (f"   -> {st}" if st else "")
            if line != last[0]:
                print(time.strftime("%H:%M:%S"), line); last[0] = line
        await c.start_notify(NOTIFY, on_data)
        print("Listening (status request every 1 s). Walk, change speed from the remote, stop. Ctrl+C to quit.")
        while c.is_connected:
            await c.write_gatt_char(WRITE, ASK_STATS, response=False)
            await asyncio.sleep(1)
        print("Disconnected.")

if __name__ == "__main__":
    pkt = bytes.fromhex("f8a2015001000064000032000096500000000afd")
    assert parse(pkt) == {"state": 1, "kmh": 8.0, "time": 100, "dist": 50, "steps": 150, "target": 8.0}, parse(pkt)
    if "--selftest" not in sys.argv:
        asyncio.run(main())
