#include "Bridge.h"

namespace treadpod {
namespace {
constexpr TimeMs kWatchGracePeriod = 30000;
constexpr TimeMs kIdleRelease = 180000;
constexpr TimeMs kStatusTimeout = 10000;
constexpr int kFallbackSpm = 100;
}  // namespace

Action Bridge::tick(TimeMs now, bool watchSubscribed, ScanResult scan) {
    if (watchSubscribed) {
        lastWatchSeen_ = now;
    }
    // The COROS can briefly unsubscribe before starting a workout.
    const bool watchPresent = lastWatchSeen_ && now - *lastWatchSeen_ < kWatchGracePeriod;
    if (!watchPresent) {
        releasedForWorkout_ = false;
        if (r2Connected()) {
            release(false);
            return Action::ReleaseForWatch;
        }
        if (snapshot_.r2 == R2State::Scanning) {
            snapshot_.r2 = R2State::WaitingForWatch;
            return Action::CancelScan;
        }
        if (snapshot_.r2 != R2State::ReleasedForKsFit) {
            snapshot_.r2 = R2State::WaitingForWatch;
        }
    } else if (snapshot_.r2 == R2State::Scanning) {
        if (scan == ScanResult::Connected) {
            snapshot_ = Snapshot{};
            snapshot_.r2 = R2State::ConnectedIdle;
            cadence_.reset();
            lastStatus_ = now;
            return Action::Poll;
        }
        if (scan == ScanResult::Failed) {
            snapshot_.r2 = R2State::NotFound;
        }
    } else if (r2Connected()) {
        return Action::Poll;
    } else if (watchSubscribed && !releasedForWorkout_) {
        snapshot_.r2 = R2State::Scanning;
        return Action::StartScan;
    }
    return Action::None;
}

Action Bridge::onPollResult(TimeMs now, bool success) {
    if (!r2Connected()) {
        return Action::None;
    }
    if (!success || now - lastStatus_ >= kStatusTimeout) {
        release(false);
        return Action::ReleaseForR2Drop;
    }
    if (snapshot_.speedKmh > 0) {
        idleSince_.reset();
        snapshot_.r2 = R2State::Running;
    } else if (ranOnThisConnection_) {
        if (!idleSince_) {
            idleSince_ = now;
        }
        snapshot_.r2 = R2State::ConnectedIdle;
        if (now - *idleSince_ > kIdleRelease) {
            release(true);
            return Action::ReleaseForIdle;
        }
    }
    return Action::None;
}

void Bridge::onPacket(TimeMs now, const uint8_t* packet, std::size_t size) {
    if (!r2Connected()) {
        return;
    }
    const auto status = parseStatus(packet, size);
    if (!status || !isKnownBeltState(status->beltState)) {
        return;
    }

    lastStatus_ = now;
    snapshot_.beltState = status->beltState;
    snapshot_.steps = status->steps;
    if (!status->isRunning()) {
        snapshot_.speedKmh = 0;
        snapshot_.cadenceSpm = 0;
        snapshot_.r2 = R2State::ConnectedIdle;
        return;
    }

    int spm = cadence_.update(now, status->steps);
    if (spm == 0) {
        spm = lastNonZeroCadenceSpm_ > 0 ? lastNonZeroCadenceSpm_ : kFallbackSpm;
    }
    lastNonZeroCadenceSpm_ = spm;
    snapshot_.speedKmh = status->speedKmh;
    snapshot_.cadenceSpm = spm;
    snapshot_.r2 = R2State::Running;
    ranOnThisConnection_ = true;
    idleSince_.reset();
}

Measurement Bridge::measurement() const {
    return encodeMeasurement(snapshot_.speedKmh, snapshot_.cadenceSpm);
}

bool Bridge::r2Connected() const {
    return snapshot_.r2 == R2State::Running || snapshot_.r2 == R2State::ConnectedIdle;
}

void Bridge::release(bool blockWorkout) {
    snapshot_.r2 = blockWorkout ? R2State::ReleasedForKsFit : R2State::WaitingForWatch;
    snapshot_.speedKmh = 0;
    snapshot_.cadenceSpm = 0;
    snapshot_.beltState.reset();
    idleSince_.reset();
    ranOnThisConnection_ = false;
    lastNonZeroCadenceSpm_ = 0;
    releasedForWorkout_ = blockWorkout;
}

}  // namespace treadpod
