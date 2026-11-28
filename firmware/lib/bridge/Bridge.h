#pragma once

#include "Protocol.h"

namespace treadpod {

enum class R2State { WaitingForWatch, Scanning, NotFound, ConnectedIdle, Running, ReleasedForKsFit };
enum class ScanResult { Pending, Failed, Connected };
enum class Action { None, StartScan, CancelScan, Poll, ReleaseForWatch, ReleaseForIdle, ReleaseForR2Drop };

struct Snapshot {
    R2State r2 = R2State::WaitingForWatch;
    std::optional<uint8_t> beltState;
    double speedKmh = 0;
    int cadenceSpm = 0;
    int steps = 0;
};

class Bridge {
public:
    Action tick(TimeMs now, bool watchSubscribed, ScanResult scan = ScanResult::Pending);
    Action onPollResult(TimeMs now, bool success);
    void onPacket(TimeMs now, const uint8_t* packet, std::size_t size);

    const Snapshot& snapshot() const { return snapshot_; }
    Measurement measurement() const;

private:
    bool r2Connected() const;
    void release(bool blockWorkout);

    Snapshot snapshot_;
    CadenceCalculator cadence_;
    std::optional<TimeMs> lastWatchSeen_;
    std::optional<TimeMs> idleSince_;
    TimeMs lastStatus_ = 0;
    int lastNonZeroCadenceSpm_ = 0;
    bool ranOnThisConnection_ = false;
    bool releasedForWorkout_ = false;
};

class AdvertisingRetry {
public:
    bool due(TimeMs now) const { return retryable_ && now >= nextAttempt_; }
    void started() { nextAttempt_ = 0; }
    void failed(TimeMs now, bool retryable = true) {
        nextAttempt_ = now + 5000;
        retryable_ = retryable;
    }

private:
    TimeMs nextAttempt_ = 0;
    bool retryable_ = true;
};

}  // namespace treadpod
