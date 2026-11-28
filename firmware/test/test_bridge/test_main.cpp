#include <unity.h>

#include <Bridge.h>

#include <array>

using namespace treadpod;

void setUp() {}
void tearDown() {}

namespace {
constexpr std::array<uint8_t, kStatusSize> sample = {
    0xf8, 0xa2, 1, 80, 1, 0, 0, 100, 0, 0, 50, 0, 0, 150, 80, 0, 0, 0, 10, 0xfd};

void emit(Bridge& bridge, TimeMs now, uint8_t state, uint8_t speed, int steps) {
    auto packet = sample;
    packet[2] = state;
    packet[3] = speed;
    packet[11] = static_cast<uint8_t>(steps >> 16);
    packet[12] = static_cast<uint8_t>(steps >> 8);
    packet[13] = static_cast<uint8_t>(steps);
    bridge.onPacket(now, packet.data(), packet.size());
}

void connect(Bridge& bridge) {
    TEST_ASSERT_TRUE(bridge.tick(0, true) == Action::StartScan);
    TEST_ASSERT_TRUE(bridge.tick(1000, true, ScanResult::Connected) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.onPollResult(1000, true) == Action::None);
}

void parses_valid_r2_status() {
    const auto status = parseStatus(sample.data(), sample.size());
    TEST_ASSERT_TRUE(status.has_value());
    TEST_ASSERT_EQUAL_UINT8(1, status->beltState);
    TEST_ASSERT_TRUE(status->isRunning());
    TEST_ASSERT_DOUBLE_WITHIN(0.001, 8.0, status->speedKmh);
    TEST_ASSERT_EQUAL_INT(100, status->timeSeconds);
    TEST_ASSERT_EQUAL_INT(50, status->distanceDecameters);
    TEST_ASSERT_DOUBLE_WITHIN(0.001, 0.50, status->distanceKm());
    TEST_ASSERT_EQUAL_INT(150, status->steps);
    TEST_ASSERT_DOUBLE_WITHIN(0.001, 8.0, status->targetSpeedKmh);
}

void rejects_invalid_r2_status() {
    auto packet = sample;
    TEST_ASSERT_FALSE(parseStatus(packet.data(), 4).has_value());
    packet[0] = 0xa8;
    TEST_ASSERT_FALSE(parseStatus(packet.data(), packet.size()).has_value());
    packet = sample;
    packet[19] = 0xff;
    TEST_ASSERT_FALSE(parseStatus(packet.data(), packet.size()).has_value());
    packet = sample;
    packet[1] = 0xa3;
    TEST_ASSERT_FALSE(parseStatus(packet.data(), packet.size()).has_value());
}

void matches_king_smith_prefix_and_read_only_packets() {
    TEST_ASSERT_TRUE(isKingSmithName("KS-SC-BLR2C"));
    TEST_ASSERT_TRUE(isKingSmithName("ks-R2"));
    TEST_ASSERT_FALSE(isKingSmithName(""));
    TEST_ASSERT_FALSE(isKingSmithName("K"));
    TEST_ASSERT_FALSE(isKingSmithName("R2-KS"));

    const uint8_t askStats[] = {0xf7, 0xa2, 0, 0, 0xa2, 0xfd};
    const uint8_t feature[] = {0, 0};
    TEST_ASSERT_EQUAL_UINT8_ARRAY(askStats, kAskStats.data(), kAskStats.size());
    TEST_ASSERT_EQUAL_UINT8_ARRAY(feature, kRscFeature.data(), kRscFeature.size());
}

void matches_verified_rsc_bytes() {
    const uint8_t at8[] = {0x04, 0x39, 0x02, 0x55};
    const uint8_t at10[] = {0x04, 0xc7, 0x02, 0x55};
    const auto speed8 = encodeMeasurement(8.0, 170);
    const auto speed10 = encodeMeasurement(10.0, 170);
    TEST_ASSERT_EQUAL_UINT8_ARRAY(at8, speed8.data(), speed8.size());
    TEST_ASSERT_EQUAL_UINT8_ARRAY(at10, speed10.data(), speed10.size());
    const auto slow = encodeMeasurement(2.0, 100);
    TEST_ASSERT_EQUAL_UINT8(142, slow[1]);
    TEST_ASSERT_EQUAL_UINT8(0, slow[2]);
}

void halves_rsc_cadence_rounding_to_even_and_caps_it() {
    TEST_ASSERT_EQUAL_UINT8(95, toRscCadence(190));
    TEST_ASSERT_EQUAL_UINT8(127, toRscCadence(300));
    TEST_ASSERT_EQUAL_UINT8(0, toRscCadence(0));
    TEST_ASSERT_EQUAL_UINT8(0, toRscCadence(-5));
    TEST_ASSERT_EQUAL_UINT8(50, toRscCadence(101));
    TEST_ASSERT_EQUAL_UINT8(52, toRscCadence(103));
}

void cadence_handles_window_gap_and_counter_reset() {
    CadenceCalculator cadence;
    TEST_ASSERT_EQUAL_INT(0, cadence.update(0, 0));
    TEST_ASSERT_EQUAL_INT(120, cadence.update(5000, 10));
    TEST_ASSERT_EQUAL_INT(120, cadence.update(10000, 20));
    TEST_ASSERT_EQUAL_INT(80, cadence.update(25000, 40));
    TEST_ASSERT_EQUAL_INT(0, cadence.update(26000, 30));
    cadence.reset();
    TEST_ASSERT_EQUAL_INT(0, cadence.update(27000, 0));
    TEST_ASSERT_EQUAL_INT(2, cadence.update(51000, 1));  // 2.5 rounds to even.
}

void subscription_starts_r2_and_running_updates_measurement() {
    Bridge bridge;
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::WaitingForWatch);
    TEST_ASSERT_TRUE(bridge.tick(0, false) == Action::None);
    connect(bridge);

    emit(bridge, 1100, 1, 75, 20);
    TEST_ASSERT_EQUAL_INT(100, bridge.snapshot().cadenceSpm);
    emit(bridge, 6100, 1, 75, 34);
    TEST_ASSERT_TRUE(bridge.tick(7000, true) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.onPollResult(7000, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::Running);
    TEST_ASSERT_DOUBLE_WITHIN(0.001, 7.5, bridge.snapshot().speedKmh);
    TEST_ASSERT_EQUAL_INT(168, bridge.snapshot().cadenceSpm);
    const uint8_t expected[] = {0x04, 0x15, 0x02, 84};
    const auto measurement = bridge.measurement();
    TEST_ASSERT_EQUAL_UINT8_ARRAY(expected, measurement.data(), measurement.size());
}

void short_watch_gap_keeps_r2_polling_until_grace_expires() {
    Bridge bridge;
    connect(bridge);
    emit(bridge, 1500, 1, 80, 1600);
    TEST_ASSERT_TRUE(bridge.tick(2000, true) == Action::Poll);
    for (TimeMs now = 3000; now <= 17000; now += 1000) {
        TEST_ASSERT_TRUE(bridge.tick(now, false) == Action::Poll);
        emit(bridge, now, 1, 80, 1600);
        TEST_ASSERT_TRUE(bridge.onPollResult(now, true) == Action::None);
    }
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::Running);
    TEST_ASSERT_EQUAL_DOUBLE(8.0, bridge.snapshot().speedKmh);
    TEST_ASSERT_TRUE(bridge.tick(17001, true) == Action::Poll);

    for (TimeMs now = 18000; now <= 47000; now += 1000) {
        TEST_ASSERT_TRUE(bridge.tick(now, false) == Action::Poll);
        emit(bridge, now, 1, 80, 1600);
        TEST_ASSERT_TRUE(bridge.onPollResult(now, true) == Action::None);
    }
    TEST_ASSERT_TRUE(bridge.tick(47001, false) == Action::ReleaseForWatch);
    TEST_ASSERT_TRUE(bridge.tick(47002, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::WaitingForWatch);
    TEST_ASSERT_FALSE(bridge.snapshot().beltState.has_value());
    TEST_ASSERT_EQUAL_INT(1600, bridge.snapshot().steps);
    const uint8_t zero[] = {0x04, 0, 0, 0};
    const auto measurement = bridge.measurement();
    TEST_ASSERT_EQUAL_UINT8_ARRAY(zero, measurement.data(), measurement.size());
    TEST_ASSERT_TRUE(bridge.tick(48000, true) == Action::StartScan);
}

void idle_release_requires_more_than_180_seconds_and_a_new_workout() {
    Bridge bridge;
    connect(bridge);
    emit(bridge, 1500, 1, 60, 300);
    TEST_ASSERT_TRUE(bridge.tick(2000, true) == Action::Poll);
    emit(bridge, 2500, 0, 0, 300);
    TEST_ASSERT_TRUE(bridge.tick(3000, true) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.onPollResult(3000, true) == Action::None);
    emit(bridge, 182999, 0, 0, 300);
    TEST_ASSERT_TRUE(bridge.tick(183000, true) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.onPollResult(183000, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(184000, true) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.onPollResult(184000, true) == Action::ReleaseForIdle);
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::ReleasedForKsFit);
    TEST_ASSERT_TRUE(bridge.tick(189000, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(204000, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(205000, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(234999, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(235000, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(265000, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(266000, true) == Action::StartScan);
}

void running_packet_before_idle_decision_prevents_release() {
    Bridge bridge;
    connect(bridge);
    emit(bridge, 1500, 1, 60, 10);
    emit(bridge, 2500, 0, 0, 10);
    TEST_ASSERT_TRUE(bridge.onPollResult(3000, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(184000, true) == Action::Poll);
    emit(bridge, 184001, 1, 60, 20);
    TEST_ASSERT_TRUE(bridge.onPollResult(184002, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::Running);
}

void no_idle_release_before_running_and_a_resume_resets_idle() {
    Bridge bridge;
    connect(bridge);
    emit(bridge, 2000, 0, 0, 0);
    emit(bridge, 182999, 0, 0, 0);
    TEST_ASSERT_TRUE(bridge.onPollResult(183000, true) == Action::None);
    emit(bridge, 184000, 1, 60, 10);
    emit(bridge, 185000, 3, 40, 10);
    TEST_ASSERT_TRUE(bridge.onPollResult(185000, true) == Action::None);
    emit(bridge, 190000, 1, 60, 22);
    emit(bridge, 191000, 0, 0, 22);
    TEST_ASSERT_TRUE(bridge.onPollResult(191000, true) == Action::None);
    emit(bridge, 365999, 0, 0, 22);
    TEST_ASSERT_TRUE(bridge.onPollResult(366000, true) == Action::None);
}

void stall_and_unknown_state_keep_speed_and_last_cadence() {
    Bridge bridge;
    connect(bridge);
    emit(bridge, 1100, 1, 65, 100);
    emit(bridge, 6100, 1, 65, 114);
    TEST_ASSERT_EQUAL_INT(168, bridge.snapshot().cadenceSpm);
    emit(bridge, 21100, 1, 65, 114);
    TEST_ASSERT_EQUAL_INT(168, bridge.snapshot().cadenceSpm);
    emit(bridge, 22100, 15, 0, 120);
    TEST_ASSERT_DOUBLE_WITHIN(0.001, 6.5, bridge.snapshot().speedKmh);
    TEST_ASSERT_EQUAL_INT(168, bridge.snapshot().cadenceSpm);
    TEST_ASSERT_EQUAL_INT(114, bridge.snapshot().steps);
    TEST_ASSERT_EQUAL_UINT8(1, *bridge.snapshot().beltState);
}

void non_running_states_zero_speed_and_cadence_and_hold_last_on_resume() {
    Bridge bridge;
    connect(bridge);
    emit(bridge, 1100, 1, 65, 100);
    emit(bridge, 6100, 1, 65, 114);
    for (auto state : std::array<uint8_t, 6>{0, 3, 6, 7, 8, 9}) {
        emit(bridge, 7000, state, 65, 114);
        TEST_ASSERT_EQUAL_DOUBLE(0.0, bridge.snapshot().speedKmh);
        TEST_ASSERT_EQUAL_INT(0, bridge.snapshot().cadenceSpm);
        TEST_ASSERT_EQUAL_UINT8(state, *bridge.snapshot().beltState);
    }
    emit(bridge, 25000, 1, 65, 114);
    TEST_ASSERT_EQUAL_INT(168, bridge.snapshot().cadenceSpm);
}

void in_flight_scan_connects_during_watch_grace() {
    Bridge bridge;
    TEST_ASSERT_TRUE(bridge.tick(0, true) == Action::StartScan);
    TEST_ASSERT_TRUE(bridge.tick(1000, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::Scanning);
    TEST_ASSERT_TRUE(bridge.tick(15000, false, ScanResult::Connected) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::ConnectedIdle);
    TEST_ASSERT_TRUE(bridge.tick(16000, true) == Action::Poll);
}

void in_flight_scan_is_cancelled_at_watch_grace_expiry() {
    Bridge bridge;
    TEST_ASSERT_TRUE(bridge.tick(0, true) == Action::StartScan);
    TEST_ASSERT_TRUE(bridge.tick(29999, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(30000, false, ScanResult::Connected) == Action::CancelScan);
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::WaitingForWatch);
    TEST_ASSERT_TRUE(bridge.tick(30001, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(31000, true) == Action::StartScan);
}

void failed_scan_and_r2_drop_retry_only_when_subscribed() {
    Bridge bridge;
    TEST_ASSERT_TRUE(bridge.tick(0, true) == Action::StartScan);
    TEST_ASSERT_TRUE(bridge.tick(10000, false, ScanResult::Failed) == Action::None);
    TEST_ASSERT_TRUE(bridge.snapshot().r2 == R2State::NotFound);
    TEST_ASSERT_TRUE(bridge.tick(11000, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(12000, true) == Action::StartScan);
    TEST_ASSERT_TRUE(bridge.tick(13000, true, ScanResult::Connected) == Action::Poll);
    emit(bridge, 13100, 1, 60, 10);
    TEST_ASSERT_TRUE(bridge.tick(14000, false) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.onPollResult(14000, false) == Action::ReleaseForR2Drop);
    TEST_ASSERT_EQUAL_INT(0, bridge.snapshot().cadenceSpm);
    TEST_ASSERT_TRUE(bridge.tick(15000, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(16000, true) == Action::StartScan);
    TEST_ASSERT_TRUE(bridge.tick(17000, true, ScanResult::Connected) == Action::Poll);
    TEST_ASSERT_EQUAL_INT(0, bridge.snapshot().steps);
}

void no_first_status_times_out_ten_seconds_after_connection() {
    Bridge bridge;
    connect(bridge);
    TEST_ASSERT_TRUE(bridge.onPollResult(10999, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.onPollResult(11000, true) == Action::ReleaseForR2Drop);
}

void valid_status_refreshes_timeout_and_silence_clears_speed() {
    Bridge bridge;
    connect(bridge);
    emit(bridge, 2000, 0, 0, 10);
    emit(bridge, 9000, 1, 60, 20);
    TEST_ASSERT_TRUE(bridge.onPollResult(12000, true) == Action::None);
    auto corrupt = sample;
    corrupt[19] = 0xff;
    bridge.onPacket(17000, corrupt.data(), corrupt.size());
    emit(bridge, 18000, 15, 0, 20);
    TEST_ASSERT_TRUE(bridge.onPollResult(18999, true) == Action::None);
    TEST_ASSERT_EQUAL_DOUBLE(6.0, bridge.snapshot().speedKmh);
    TEST_ASSERT_TRUE(bridge.tick(19000, false) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.onPollResult(19000, true) == Action::ReleaseForR2Drop);
    TEST_ASSERT_EQUAL_DOUBLE(0.0, bridge.snapshot().speedKmh);
    TEST_ASSERT_EQUAL_INT(0, bridge.snapshot().cadenceSpm);
    TEST_ASSERT_FALSE(bridge.snapshot().beltState.has_value());
    TEST_ASSERT_TRUE(bridge.tick(20000, false) == Action::None);
    TEST_ASSERT_TRUE(bridge.tick(21000, true) == Action::StartScan);
    TEST_ASSERT_TRUE(bridge.tick(22000, true, ScanResult::Connected) == Action::Poll);
    TEST_ASSERT_TRUE(bridge.onPollResult(31999, true) == Action::None);
    TEST_ASSERT_TRUE(bridge.onPollResult(32000, true) == Action::ReleaseForR2Drop);
}

void advertising_retries_transient_failures_after_five_seconds() {
    AdvertisingRetry retry;
    TEST_ASSERT_TRUE(retry.due(0));
    retry.failed(0);
    TEST_ASSERT_FALSE(retry.due(4999));
    TEST_ASSERT_TRUE(retry.due(5000));
    retry.failed(5000);
    TEST_ASSERT_FALSE(retry.due(9999));
    TEST_ASSERT_TRUE(retry.due(10000));
    retry.started();
    TEST_ASSERT_TRUE(retry.due(10001));  // Ready to restart immediately after a disconnect.
}

void advertising_does_not_retry_permanent_failures() {
    AdvertisingRetry retry;
    retry.failed(0, false);
    TEST_ASSERT_FALSE(retry.due(20000));
}
}  // namespace

int main() {
    UNITY_BEGIN();
    RUN_TEST(parses_valid_r2_status);
    RUN_TEST(rejects_invalid_r2_status);
    RUN_TEST(matches_king_smith_prefix_and_read_only_packets);
    RUN_TEST(matches_verified_rsc_bytes);
    RUN_TEST(halves_rsc_cadence_rounding_to_even_and_caps_it);
    RUN_TEST(cadence_handles_window_gap_and_counter_reset);
    RUN_TEST(subscription_starts_r2_and_running_updates_measurement);
    RUN_TEST(short_watch_gap_keeps_r2_polling_until_grace_expires);
    RUN_TEST(idle_release_requires_more_than_180_seconds_and_a_new_workout);
    RUN_TEST(running_packet_before_idle_decision_prevents_release);
    RUN_TEST(no_idle_release_before_running_and_a_resume_resets_idle);
    RUN_TEST(stall_and_unknown_state_keep_speed_and_last_cadence);
    RUN_TEST(non_running_states_zero_speed_and_cadence_and_hold_last_on_resume);
    RUN_TEST(in_flight_scan_connects_during_watch_grace);
    RUN_TEST(in_flight_scan_is_cancelled_at_watch_grace_expiry);
    RUN_TEST(failed_scan_and_r2_drop_retry_only_when_subscribed);
    RUN_TEST(no_first_status_times_out_ten_seconds_after_connection);
    RUN_TEST(valid_status_refreshes_timeout_and_silence_clears_speed);
    RUN_TEST(advertising_retries_transient_failures_after_five_seconds);
    RUN_TEST(advertising_does_not_retry_permanent_failures);
    return UNITY_END();
}
