#include "Protocol.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace treadpod {
namespace {

int readUInt24(const uint8_t* bytes) {
    return (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
}

double roundToEven(double value) {
    const double lower = std::floor(value);
    const double fraction = value - lower;
    return fraction > 0.5 || (fraction == 0.5 && std::fmod(lower, 2.0) != 0) ? lower + 1 : lower;
}

}  // namespace

std::optional<R2Status> parseStatus(const uint8_t* packet, std::size_t size) {
    if (size != kStatusSize || packet[0] != 0xf8 || packet[1] != 0xa2 || packet[19] != 0xfd) {
        return std::nullopt;
    }
    return R2Status{packet[2], packet[3] / 10.0, readUInt24(packet + 5), readUInt24(packet + 8),
                    readUInt24(packet + 11), packet[14] / 10.0};
}

bool isKingSmithName(std::string_view name) {
    return name.size() >= 2 && (name[0] == 'K' || name[0] == 'k') && (name[1] == 'S' || name[1] == 's');
}

bool isKnownBeltState(uint8_t state) {
    return state == 0 || state == 1 || state == 3 || (state >= 6 && state <= 9);
}

uint8_t toRscCadence(int stepsPerMinute) {
    // COROS doubles the stride count and wraps values of 128 or more.
    return static_cast<uint8_t>(roundToEven(std::clamp(stepsPerMinute, 0, 254) / 2.0));
}

Measurement encodeMeasurement(double speedKmh, int stepsPerMinute) {
    const double scaled = std::min(std::max(0.0, speedKmh) / 3.6 * 256.0, 65535.0);
    const auto speed = static_cast<uint16_t>(roundToEven(scaled));
    return {0x04, static_cast<uint8_t>(speed), static_cast<uint8_t>(speed >> 8), toRscCadence(stepsPerMinute)};
}

int CadenceCalculator::update(TimeMs now, int steps) {
    constexpr TimeMs window = 10000;
    if (!points_.empty() && steps < points_.back().steps) {
        reset();
    }
    points_.push_back({now, steps});

    // Keep one point at or before the window start, and at least two samples.
    while (points_.size() > 2 && now >= points_[1].time && now - points_[1].time >= window) {
        points_.pop_front();
    }
    const auto& first = points_.front();
    if (now <= first.time || steps < first.steps) {
        return 0;
    }
    const double spm = (static_cast<double>(steps) - first.steps) * 60000.0 / (now - first.time);
    return static_cast<int>(roundToEven(std::min(spm, static_cast<double>(std::numeric_limits<int>::max()))));
}

}  // namespace treadpod
