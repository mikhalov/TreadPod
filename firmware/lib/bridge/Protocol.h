#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <optional>
#include <string_view>

namespace treadpod {

using TimeMs = uint64_t;
using Measurement = std::array<uint8_t, 4>;

inline constexpr std::size_t kStatusSize = 20;
inline constexpr std::array<uint8_t, 6> kAskStats = {0xf7, 0xa2, 0x00, 0x00, 0xa2, 0xfd};
inline constexpr std::array<uint8_t, 2> kRscFeature = {0x00, 0x00};

struct R2Status {
    uint8_t beltState;
    double speedKmh;
    int timeSeconds;
    int distanceDecameters;
    int steps;
    double targetSpeedKmh;

    bool isRunning() const { return beltState == 1; }
    double distanceKm() const { return distanceDecameters / 100.0; }
};

std::optional<R2Status> parseStatus(const uint8_t* packet, std::size_t size);
bool isKingSmithName(std::string_view name);
bool isKnownBeltState(uint8_t state);
uint8_t toRscCadence(int stepsPerMinute);
Measurement encodeMeasurement(double speedKmh, int stepsPerMinute);

class CadenceCalculator {
public:
    int update(TimeMs now, int steps);
    void reset() { points_.clear(); }

private:
    struct Point {
        TimeMs time;
        int steps;
    };
    std::deque<Point> points_;
};

}  // namespace treadpod
