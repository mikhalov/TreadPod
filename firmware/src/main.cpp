#include <Arduino.h>
#include <NimBLEDevice.h>

#include <array>
#include <atomic>

namespace {
constexpr char kDeviceName[] = "TreadPod";
constexpr uint8_t kCadence = 45;  // The watch displays 90 steps/min.
using Measurement = std::array<uint8_t, 4>;

constexpr Measurement encodeMeasurement(float kmh, uint8_t cadence) {
    // The speed field uses units of 1/256 m/s. Round the nonnegative speed.
    const auto speed = static_cast<uint16_t>(kmh / 3.6f * 256 + 0.5f);
    return {0x04, static_cast<uint8_t>(speed), static_cast<uint8_t>(speed >> 8), cadence};
}

NimBLECharacteristic* measurement = nullptr;
std::atomic<bool> subscribed{false};

class ServerCallbacks : public NimBLEServerCallbacks {
    void onConnect(NimBLEServer*, NimBLEConnInfo& peer) override {
        Serial.printf("Connect %s\n", peer.getAddress().toString().c_str());
    }

    void onDisconnect(NimBLEServer*, NimBLEConnInfo& peer, int) override {
        subscribed = false;
        Serial.printf("Disconnect %s\n", peer.getAddress().toString().c_str());
    }
};

class MeasurementCallbacks : public NimBLECharacteristicCallbacks {
    void onSubscribe(NimBLECharacteristic*, NimBLEConnInfo& peer, uint16_t value) override {
        const bool enabled = (value & 0x01) != 0;
        subscribed = enabled;
        Serial.printf("%s %s\n", enabled ? "Subscribe" : "Unsubscribe",
                      peer.getAddress().toString().c_str());
    }
};

ServerCallbacks serverCallbacks;
MeasurementCallbacks measurementCallbacks;
}  // namespace

void setup() {
    Serial.begin(115200);

    if (encodeMeasurement(8, kCadence) != Measurement{0x04, 0x39, 0x02, 0x2D} ||
        encodeMeasurement(10, kCadence) != Measurement{0x04, 0xC7, 0x02, 0x2D}) {
        Serial.println("FAIL: RSC measurement encoding");
    }

    if (!NimBLEDevice::init(kDeviceName) || !NimBLEDevice::setOwnAddrType(BLE_OWN_ADDR_PUBLIC)) {
        Serial.println("FAIL: BLE public address initialization");
        return;
    }
    Serial.printf("BLE MAC %s\n", NimBLEDevice::getAddress().toString().c_str());

    auto* server = NimBLEDevice::createServer();
    server->setCallbacks(&serverCallbacks, false);
    server->advertiseOnDisconnect(true);

    auto* service = server->createService("1814");
    measurement = service->createCharacteristic("2A53", NIMBLE_PROPERTY::NOTIFY);
    measurement->setCallbacks(&measurementCallbacks);

    constexpr uint8_t featureValue[] = {0x00, 0x00};
    service->createCharacteristic("2A54", NIMBLE_PROPERTY::READ)
        ->setValue(featureValue, sizeof(featureValue));

    auto* advertising = NimBLEDevice::getAdvertising();
    advertising->setConnectableMode(BLE_GAP_CONN_MODE_UND);
    advertising->addServiceUUID(service->getUUID());
    advertising->setName(kDeviceName);
    if (!advertising->start()) {
        Serial.println("FAIL: BLE advertising");
    }
}

void loop() {
    static TickType_t lastMeasurement = xTaskGetTickCount();
    static unsigned second = 0;
    vTaskDelayUntil(&lastMeasurement, pdMS_TO_TICKS(1000));

    second = (second + 1) % 40;
    const unsigned kmh = second < 20 ? 8 : 10;
    const bool hasSubscriber = subscribed.load();
    if (hasSubscriber) {
        const auto packet = encodeMeasurement(kmh, kCadence);
        measurement->notify(packet.data(), packet.size());
    }
    Serial.printf("%u km/h subscribed=%s\n", kmh, hasSubscriber ? "yes" : "no");
}
