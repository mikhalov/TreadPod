# TreadPod bridge: KingSmith R2 (read-only) -> RSC foot pod for the COROS watch.
# Lifecycle: the RSC service stays up for the whole PC session (restart = new address = re-pair on the watch).
# The R2 is held only while the watch is subscribed (workout screen open); it is released when the watch
# unsubscribes (workout saved) or the belt has been stopped > IDLE_RELEASE_S, so KS Fit can connect afterwards.
import asyncio, time
from collections import deque
from bleak import BleakScanner, BleakClient
from treadpod_test import SVC, MEAS, FEAT, measurement, buf, params
from r2_probe import NOTIFY, WRITE, ASK_STATS, parse
from winrt.windows.devices.bluetooth.genericattributeprofile import (
    GattServiceProvider, GattCharacteristicProperties, GattServiceProviderAdvertisingParameters)

IDLE_RELEASE_S = 180      # belt stopped this long after running -> release the R2 (short pauses must not end it)
CADENCE_WINDOW_S = 10     # steps-per-minute averaged over this window
KNOWN_STATES = {0, 1, 3, 6, 7, 8, 9}   # stopped, running, slowing, countdown
WATCH_GRACE_S = 30        # watch absent this long -> workout saved -> release the R2
# Pod cadence 0 while the belt runs made the watch drop the pod speed and use its own (16 km/h instead of 6).
# ponytail: fixed fallback until the R2 gives a first cadence; estimate from speed if starts look wrong.
DEFAULT_SPM = 100

def rsc_cadence(spm):
    # COROS treats RSC cadence as strides/min: sending 95 showed 191 spm, and values >= 128 wrapped (130 -> 2).
    return min(round(spm / 2), 127)

class Cadence:
    """Steps per minute from the R2's cumulative step counter."""
    def __init__(self, window=CADENCE_WINDOW_S):
        self.window, self.points = window, deque()
    def update(self, t, steps):
        if self.points and steps < self.points[-1][1]:
            self.points.clear()     # counter reset (new R2 session)
        self.points.append((t, steps))
        while len(self.points) > 2 and t - self.points[1][0] >= self.window:
            self.points.popleft()   # keep one point at/before the window start (and never drop below two)
        (t0, s0) = self.points[0]
        return 0 if t == t0 or steps < s0 else round((steps - s0) * 60 / (t - t0))

state = {"kmh": 0.0, "spm": 0, "r2": "idle", "belt": None, "steps": 0}

async def rsc_server():
    res = await GattServiceProvider.create_async(SVC)
    if res.service_provider is None:
        raise SystemExit(f"GATT service not created: {res.error.name} (RADIO_NOT_AVAILABLE = Bluetooth is off)")
    provider = res.service_provider
    meas = (await provider.service.create_characteristic_async(
        MEAS, params(GattCharacteristicProperties.NOTIFY))).characteristic
    await provider.service.create_characteristic_async(
        FEAT, params(GattCharacteristicProperties.READ, b"\x00\x00"))
    adv = GattServiceProviderAdvertisingParameters()
    adv.is_discoverable = adv.is_connectable = True
    provider.start_advertising_with_parameters(adv)   # start_advertising(adv) -> ABORTED on this pywinrt
    await asyncio.sleep(1)
    return provider, meas

async def hold_r2(watch_subscribed):   # watch_subscribed: present within the grace period
    """Connect to the R2 and feed `state` until the watch leaves or the belt idles too long."""
    state["r2"] = "scanning"
    dev = await BleakScanner.find_device_by_filter(lambda d, _: (d.name or "").upper().startswith("KS"), timeout=10)
    if dev is None:
        state["r2"] = "not found (KS Fit connected or treadmill off?)"
        return False   # retry next loop
    cad, ran, idle_since = Cadence(), False, None
    def on_data(_, data):
        st = parse(data)
        if st is None or st["state"] not in KNOWN_STATES:
            return     # e.g. a one-off state 15 seen 2026-11-25: keep the previous values
        running = st["state"] == 1
        spm = cad.update(time.monotonic(), st["steps"]) if running else 0
        if running and spm == 0:
            spm = state["spm"] or DEFAULT_SPM   # R2 step counter stalls sometimes: hold the last cadence
        state.update(belt=st["state"], steps=st["steps"], kmh=st["kmh"] if running else 0.0, spm=spm)
    async with BleakClient(dev) as c:
        state["r2"] = f"connected {dev.name}"
        await c.start_notify(NOTIFY, on_data)
        while c.is_connected and watch_subscribed():
            await c.write_gatt_char(WRITE, ASK_STATS, response=False)
            await asyncio.sleep(1)
            if state["kmh"] > 0:
                ran, idle_since = True, None
            elif ran:
                idle_since = idle_since or time.monotonic()
                if time.monotonic() - idle_since > IDLE_RELEASE_S:
                    break
        idle = idle_since is not None and time.monotonic() - idle_since > IDLE_RELEASE_S
        reason = "watch left" if not watch_subscribed() else "belt idle" if idle else "R2 dropped"
    state.update(kmh=0.0, spm=0, r2=f"released ({reason})")
    return idle   # only an idle release blocks reconnecting during this watch session

async def main():
    provider, meas = await rsc_server()
    print("Foot pod advertising:", provider.advertisement_status.name, "- Ctrl+C to stop.")
    subscribed = lambda: len(meas.subscribed_clients) > 0
    # The watch drops and re-adds the pod for ~15 s right before the workout starts (seen in every test),
    # so a short absence must not release the R2.
    last_seen = -WATCH_GRACE_S
    watch_present = lambda: time.monotonic() - last_seen < WATCH_GRACE_S
    r2_task, released_for_this_session = None, False

    while True:
        if subscribed():
            last_seen = time.monotonic()
        if not watch_present():
            released_for_this_session = False      # next subscription = next workout
        elif subscribed() and r2_task is None and not released_for_this_session:
            r2_task = asyncio.create_task(hold_r2(watch_present))
        if r2_task and r2_task.done():
            # belt-idle release -> wait for the next workout; not found / dropped -> retry next loop
            released_for_this_session = r2_task.result() and watch_present()
            r2_task = None
        await meas.notify_value_async(buf(measurement(state["kmh"], rsc_cadence(state["spm"]))))
        print(f"\r{time.strftime('%H:%M:%S')} watch {'on ' if subscribed() else 'off'} | R2 {state['r2']} | "
              f"belt {state['belt']} {state['kmh']:.1f} km/h {state['spm']} spm steps {state['steps']}   ", end="")
        await asyncio.sleep(1)

if __name__ == "__main__":
    import sys
    c = Cadence(window=10)
    assert c.update(0, 0) == 0 and c.update(5, 10) == 120 and c.update(10, 20) == 120
    assert c.update(25, 40) == 80           # 15 s gap: falls back to the last point before the gap
    assert c.update(26, 30) == 0            # counter reset (new R2 session) -> 0, not negative
    assert rsc_cadence(190) == 95 and rsc_cadence(300) == 127 and rsc_cadence(0) == 0
    if "--selftest" not in sys.argv:
        asyncio.run(main())
