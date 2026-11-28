#include <Arduino.h>
#include <ESPmDNS.h>
#include <NimBLEDevice.h>
#include <WebServer.h>
#include <WiFi.h>
#include <esp_sntp.h>
#include <esp_timer.h>
#include <freertos/queue.h>
#include <freertos/semphr.h>

#ifdef USING_NIMBLE_ARDUINO_HEADERS
#include <nimble/porting/nimble/include/nimble/nimble_port.h>
#else
#include <nimble/nimble_port.h>
#endif

#include <Bridge.h>
#include "wifi_secrets.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <ctime>
#include <type_traits>
#include <utility>

namespace {
using namespace treadpod;

static_assert(CONFIG_BT_NIMBLE_ROLE_CENTRAL && CONFIG_BT_NIMBLE_ROLE_OBSERVER &&
              CONFIG_BT_NIMBLE_ROLE_PERIPHERAL && CONFIG_BT_NIMBLE_MAX_CONNECTIONS >= 2,
              "TreadPod needs BLE central and peripheral roles and two simultaneous links");

constexpr char kDeviceName[] = "TreadPod";
constexpr char kHostname[] = "treadpod";
constexpr char kKyivTimezone[] = "EET-2EEST,M3.5.0/3,M10.5.0/4";
constexpr uint32_t kScanTimeoutMs = 10000;
constexpr TimeMs kConnectDeadlineMs = 15000;
constexpr TimeMs kTickIntervalMs = 1000;

TimeMs nowMs() { return static_cast<TimeMs>(esp_timer_get_time()) / 1000; }

enum class WatchState { Disconnected, Connected, Subscribed };
NimBLEAdvertising* advertising = nullptr;
AdvertisingRetry advertisingRetry;
bool advertisingStarted = false;

class WatchPeers : public NimBLEServerCallbacks, public NimBLECharacteristicCallbacks {
public:
    bool allocate() {
        measurements_ = xQueueCreate(1, sizeof(Measurement));
        return measurements_ != nullptr;
    }

    void configure(NimBLECharacteristic* measurement) {
        measurement_ = measurement;
        ble_npl_event_init(&notifyEvent_, sendMeasurement, this);
    }

    WatchState state() const { return state_.load(); }
    unsigned subscribedHandles() const { return subscribedHandles_.load(); }
    unsigned connects() const { return connects_.load(); }
    unsigned disconnects() const { return disconnects_.load(); }

    void notify(const Measurement& payload) {
        xQueueOverwrite(measurements_, &payload);
        ble_npl_eventq_put(nimble_port_get_dflt_eventq(), &notifyEvent_);
    }

private:
    struct Peer {
        uint16_t handle = BLE_HS_CONN_HANDLE_NONE;
        bool subscribed = false;
        bool connectReported = false;
    };

    void onConnect(NimBLEServer*, NimBLEConnInfo& conn) override {
        ++connects_;
        if (auto* peer = registerPeer(conn.getConnHandle())) {
            peer->connectReported = true;
            updateState();
        }
    }

    void onDisconnect(NimBLEServer*, NimBLEConnInfo& conn, int) override {
        ++disconnects_;
        if (auto* peer = find(conn.getConnHandle())) {
            *peer = Peer{};
            updateState();
        }
    }

    void onSubscribe(NimBLECharacteristic*, NimBLEConnInfo& conn, uint16_t value) override {
        const bool subscribed = (value & 1) != 0;
        const auto handle = conn.getConnHandle();
        if (auto* peer = subscribed ? registerPeer(handle) : find(handle)) {
            peer->subscribed = subscribed;
            // GATT clears the CCCD on teardown even when GAP suppresses an early disconnect.
            if (!subscribed && !peer->connectReported) {
                *peer = Peer{};
            }
            updateState();
        }
    }

    Peer* registerPeer(uint16_t handle) {
        auto* peer = find(handle);
        if (!peer) {
            peer = find(BLE_HS_CONN_HANDLE_NONE);
            if (peer) {
                peer->handle = handle;
            }
        }
        return peer;
    }

    Peer* find(uint16_t handle) {
        const auto peer = std::find_if(peers_.begin(), peers_.end(), [handle](const Peer& entry) {
            return entry.handle == handle;
        });
        return peer == peers_.end() ? nullptr : &*peer;
    }

    void updateState() {
        bool connected = false;
        unsigned subscribed = 0;
        for (const auto& peer : peers_) {
            connected |= peer.handle != BLE_HS_CONN_HANDLE_NONE;
            subscribed += peer.subscribed;
        }
        subscribedHandles_ = subscribed;
        state_ = subscribed ? WatchState::Subscribed
                            : connected ? WatchState::Connected : WatchState::Disconnected;
    }

    static void sendMeasurement(ble_npl_event* event) {
        auto& watch = *static_cast<WatchPeers*>(ble_npl_event_get_arg(event));
        Measurement payload;
        if (xQueueReceive(watch.measurements_, &payload, 0) == pdTRUE) {
            // CCCD changes, handle reuse, and the targeted sends all run on the host task.
            for (const auto& peer : watch.peers_) {
                if (peer.subscribed && !watch.measurement_->notify(payload.data(), payload.size(), peer.handle)) {
                    Serial.println("RSC notify failed");
                }
            }
        }
    }

    std::array<Peer, CONFIG_BT_NIMBLE_MAX_CONNECTIONS> peers_{};
    std::atomic<WatchState> state_{WatchState::Disconnected};
    std::atomic<unsigned> subscribedHandles_{0};
    std::atomic<unsigned> connects_{0};
    std::atomic<unsigned> disconnects_{0};
    QueueHandle_t measurements_ = nullptr;
    NimBLECharacteristic* measurement_ = nullptr;
    ble_npl_event notifyEvent_{};
};

WatchPeers watchPeers;

class R2Link : private NimBLEScanCallbacks, private NimBLEClientCallbacks {
public:
    bool allocate() {
        candidateQueue_ = xQueueCreate(1, sizeof(Candidate));
        packetQueue_ = xQueueCreate(16, sizeof(Packet));
        packetMutex_ = xSemaphoreCreateMutex();
        connectionUpdates_ = xQueueCreate(1, sizeof(ConnectionUpdate));
        discoveryResults_ = xQueueCreate(1, sizeof(NimBLERemoteCharacteristic*));
        return candidateQueue_ && packetQueue_ && packetMutex_ && connectionUpdates_ && discoveryResults_ &&
               xTaskCreate(discoveryTask, "R2 GATT", 4096, this, 1, &discoveryTask_) == pdPASS;
    }

    bool configure() {
        if (!client_) {
            client_ = NimBLEDevice::createClient();
            if (!client_) {
                return false;
            }
            client_->setClientCallbacks(this, false);
            client_->setConnectTimeout(5000);
            // The bridge owns retries so a 0x3e disconnect always produces a terminal callback.
            client_->setConnectRetries(0);
            ble_npl_event_init(&endedEvent_, reportEnded, this);
            ble_npl_event_init(&closeEvent_, closeConnection, this);
        }
        scanner_ = NimBLEDevice::getScan();
        scanner_->setActiveScan(true);
        scanner_->setScanCallbacks(this);
        return true;
    }

    void startScan() {
        if (stage_ != Stage::Idle) {
            // The previous link is still closing. Let the bridge retry next tick.
            scanResult_ = ScanResult::Failed;
            return;
        }
        xQueueReset(candidateQueue_);
        scanEnded_ = false;
        if (!scanner_->start(kScanTimeoutMs, false, false)) {
            fail("R2 could not start scan");
            return;
        }
        scanPending_ = true;
        stage_ = Stage::Scanning;
        Serial.println("R2 scanning for KS devices");
    }

    void service() {
        auto event = ConnectionEvent::None;
        ConnectionUpdate update;
        if (xQueueReceive(connectionUpdates_, &update, 0) == pdTRUE && update.attempt == attempt_.load()) {
            event = update.event;
            if (event == ConnectionEvent::Ended) {
                attemptPending_ = false;
                writer_ = nullptr;
            }
        }
        if (scanEnded_.load()) {
            scanPending_ = false;
        }

        NimBLERemoteCharacteristic* writer = nullptr;
        if (discoveryPending_ && xQueueReceive(discoveryResults_, &writer, 0) == pdTRUE) {
            discoveryPending_ = false;
            if (stage_ == Stage::Discovering) {
                if (writer && client_->isConnected() && attemptPending_) {
                    writer_ = writer;
                    stage_ = Stage::Ready;
                    scanResult_ = ScanResult::Connected;
                    Serial.printf("R2 connected to %s (read-only FE00)\n", candidate_.name);
                } else {
                    fail("R2 FE00 discovery or FE01 subscription failed");
                }
            }
        }

        switch (stage_) {
            case Stage::Scanning: {
                // Keep scan responses available for name matching and let the host finish the scan.
                if (!scanPending_) {
                    if (xQueueReceive(candidateQueue_, &candidate_, 0) == pdTRUE) {
                        connectCandidate();
                    } else {
                        fail("R2 not found (KS Fit connected or treadmill off?)");
                    }
                }
                break;
            }
            case Stage::Connecting:
                if (event == ConnectionEvent::Ended) {
                    fail("R2 connection failed");
                } else if (nowMs() >= connectDeadline_) {
                    fail("R2 connection timed out");
                } else if (event == ConnectionEvent::Connected) {
                    stage_ = Stage::Discovering;
                    discoveryPending_ = true;
                    xTaskNotifyGive(discoveryTask_);
                }
                break;
            case Stage::Discovering:
                if (event == ConnectionEvent::Ended) {
                    fail("R2 connection dropped during discovery");
                }
                break;
            case Stage::Closing:
                finishClosing();
                break;
            case Stage::Idle:
            case Stage::Ready:
                break;
        }
    }

    ScanResult takeScanResult() { return std::exchange(scanResult_, ScanResult::Pending); }
    unsigned connects() const { return connects_.load(); }
    unsigned drops() const { return drops_.load(); }

    void drainPackets(Bridge& bridge) {
        xSemaphoreTake(packetMutex_, portMAX_DELAY);
        applyQueuedPackets(bridge);
        xSemaphoreGive(packetMutex_);
    }

    Action poll(Bridge& bridge) {
        const bool success = writer_ && client_->isConnected() &&
                             writer_->writeValue(kAskStats.data(), kAskStats.size(), false);
        // A status queued before this decision must take effect before timeout or idle release.
        xSemaphoreTake(packetMutex_, portMAX_DELAY);
        applyQueuedPackets(bridge);
        const auto action = bridge.onPollResult(nowMs(), success);
        xSemaphoreGive(packetMutex_);
        return action;
    }

    void close() {
        scanResult_ = ScanResult::Pending;
        if (stage_ == Stage::Idle || stage_ == Stage::Closing) {
            return;
        }
        stage_ = Stage::Closing;
        writer_ = nullptr;
        finishClosing();
    }

private:
    enum class Stage { Idle, Scanning, Connecting, Discovering, Ready, Closing };
    enum class ConnectionEvent { None, Connected, Ended };
    struct ConnectionUpdate {
        uint32_t attempt;
        ConnectionEvent event;
    };
    struct Candidate {
        ble_addr_t address;
        char name[32];
    };
    struct Packet {
        TimeMs time;
        uint8_t bytes[kStatusSize];
    };

    void applyQueuedPackets(Bridge& bridge) {
        Packet packet;
        while (xQueueReceive(packetQueue_, &packet, 0) == pdTRUE) {
            bridge.onPacket(packet.time, packet.bytes, sizeof(packet.bytes));
        }
    }

    void onResult(const NimBLEAdvertisedDevice* device) override {
        const auto name = device->getName();
        if (isKingSmithName(name)) {
            Candidate candidate{*device->getAddress().getBase(), {}};
            std::snprintf(candidate.name, sizeof(candidate.name), "%s", name.c_str());
            xQueueSend(candidateQueue_, &candidate, 0);
        }
    }

    void onScanEnd(const NimBLEScanResults&, int) override { scanEnded_ = true; }
    void onConnect(NimBLEClient*) override {
        ++connects_;
        const ConnectionUpdate update{attempt_.load(), ConnectionEvent::Connected};
        xQueueOverwrite(connectionUpdates_, &update);
    }
    void onConnectFail(NimBLEClient*, int) override { deferEnded(); }
    void onDisconnect(NimBLEClient*, int) override {
        if (closingAttempt_.load() != attempt_.load()) {
            ++drops_;
        }
        deferEnded();
    }

    void deferEnded() {
        // The library clears its connection state after onDisconnect returns.
        endedAttempt_ = attempt_.load();
        ble_npl_eventq_put(nimble_port_get_dflt_eventq(), &endedEvent_);
    }

    static void reportEnded(ble_npl_event* event) {
        auto& link = *static_cast<R2Link*>(ble_npl_event_get_arg(event));
        const ConnectionUpdate update{link.endedAttempt_, ConnectionEvent::Ended};
        xQueueOverwrite(link.connectionUpdates_, &update);
    }

    static void closeConnection(ble_npl_event* event) {
        auto& link = *static_cast<R2Link*>(ble_npl_event_get_arg(event));
        const auto attempt = link.attempt_.load();
        if (attempt != link.closingAttempt_.load() || attempt == link.endedAttempt_) {
            return;
        }
        if (link.client_->isConnected()) {
            link.client_->disconnect();
        } else if (link.client_->getConnHandle() == BLE_HS_CONN_HANDLE_NONE) {
            // GAP owns the link before the client receives its handle.
            const auto peer = link.client_->getPeerAddress();
            ble_gap_conn_desc conn;
            if (ble_gap_conn_find_by_addr(peer.getBase(), &conn) == 0 && conn.role == BLE_GAP_ROLE_MASTER) {
                ble_gap_terminate(conn.conn_handle, BLE_ERR_REM_USER_CONN_TERM);
            } else if (attempt != link.cancelledAttempt_ && ble_gap_conn_active() && link.client_->cancelConnect()) {
                link.cancelledAttempt_ = attempt;
            }
        }
    }

    void connectCandidate() {
        Serial.printf("R2 connecting to %s\n", candidate_.name);
        xQueueReset(packetQueue_);
        ++attempt_;
        connectDeadline_ = nowMs() + kConnectDeadlineMs;
        if (!client_->connect(NimBLEAddress(candidate_.address), true, true, false)) {
            fail("R2 could not start connection");
            return;
        }
        attemptPending_ = true;
        stage_ = Stage::Connecting;
    }

    static void discoveryTask(void* context) {
        auto& link = *static_cast<R2Link*>(context);
        for (;;) {
            ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
            auto* writer = link.discover();
            xQueueSend(link.discoveryResults_, &writer, 0);
        }
    }

    NimBLERemoteCharacteristic* discover() {
        // NimBLE's discovery and CCCD APIs block. Keep the watch's 1 Hz loop independent.
        auto* service = client_->getService("FE00");
        if (!service) {
            return nullptr;
        }
        auto* notify = service->getCharacteristic("FE01");
        auto* writer = service->getCharacteristic("FE02");
        if (!writer || !writer->canWriteNoResponse() || !notify || !notify->canNotify() ||
            !notify->getDescriptor(NimBLEUUID(uint16_t{0x2902}))) {
            return nullptr;
        }
        const bool subscribed = notify->subscribe(true, [this](NimBLERemoteCharacteristic*, uint8_t* data,
                                                               size_t size, bool) {
            if (size == kStatusSize) {
                Packet packet{nowMs(), {}};
                std::copy_n(data, size, packet.bytes);
                xSemaphoreTake(packetMutex_, portMAX_DELAY);
                if (xQueueSend(packetQueue_, &packet, 0) != pdTRUE) {
                    Packet oldest;
                    xQueueReceive(packetQueue_, &oldest, 0);
                    xQueueSend(packetQueue_, &packet, 0);
                }
                xSemaphoreGive(packetMutex_);
            }
        });
        return subscribed ? writer : nullptr;
    }

    void fail(const char* message) {
        Serial.println(message);
        close();
        scanResult_ = ScanResult::Failed;
    }

    void finishClosing() {
        if (attemptPending_) {
            closingAttempt_ = attempt_.load();
            ble_npl_eventq_put(nimble_port_get_dflt_eventq(), &closeEvent_);
        } else if (!discoveryPending_ && !scanPending_) {
            stage_ = Stage::Idle;
        }
    }

    NimBLEScan* scanner_ = nullptr;
    NimBLEClient* client_ = nullptr;
    NimBLERemoteCharacteristic* writer_ = nullptr;
    QueueHandle_t candidateQueue_ = nullptr;
    QueueHandle_t packetQueue_ = nullptr;
    SemaphoreHandle_t packetMutex_ = nullptr;
    QueueHandle_t connectionUpdates_ = nullptr;
    QueueHandle_t discoveryResults_ = nullptr;
    TaskHandle_t discoveryTask_ = nullptr;
    Candidate candidate_{};
    std::atomic<bool> scanEnded_{false};
    std::atomic<uint32_t> attempt_{0};
    std::atomic<uint32_t> closingAttempt_{0};
    std::atomic<unsigned> connects_{0};
    std::atomic<unsigned> drops_{0};
    uint32_t endedAttempt_ = 0;      // Host task only.
    uint32_t cancelledAttempt_ = 0;  // Host task only.
    ble_npl_event endedEvent_{};
    ble_npl_event closeEvent_{};
    Stage stage_ = Stage::Idle;
    ScanResult scanResult_ = ScanResult::Pending;
    TimeMs connectDeadline_ = 0;
    bool scanPending_ = false;
    bool attemptPending_ = false;
    bool discoveryPending_ = false;
};

R2Link r2;
Bridge bridge;

struct StatusReport {
    Snapshot bridge;
    unsigned subscribedHandles = 0;
    unsigned watchConnects = 0;
    unsigned watchDisconnects = 0;
    unsigned r2Connects = 0;
    unsigned r2Drops = 0;
    unsigned r2StatusTimeouts = 0;
};
static_assert(std::is_trivially_copyable_v<StatusReport>);

QueueHandle_t statusReports = nullptr;
std::atomic<bool> wifiUp{false};
std::atomic<bool> timeSynced{false};

bool configureBle() {
    if (!NimBLEDevice::init(kDeviceName) || !NimBLEDevice::setOwnAddrType(BLE_OWN_ADDR_PUBLIC) || !r2.configure()) {
        return false;
    }

    auto* server = NimBLEDevice::createServer();
    server->setCallbacks(&watchPeers, false);
    auto* service = server->createService("1814");
    auto* measurement = service->createCharacteristic("2A53", NIMBLE_PROPERTY::NOTIFY);
    watchPeers.configure(measurement);
    measurement->setCallbacks(&watchPeers);
    service->createCharacteristic("2A54", NIMBLE_PROPERTY::READ)->setValue(kRscFeature.data(), kRscFeature.size());

    advertising = NimBLEDevice::getAdvertising();
    advertising->setConnectableMode(BLE_GAP_CONN_MODE_UND);
    advertising->addServiceUUID(service->getUUID());
    advertising->setName(kDeviceName);
    Serial.printf("BLE public MAC %s\n", NimBLEDevice::getAddress().toString().c_str());
    return true;
}

void ensureAdvertising(TimeMs now, WatchState watch) {
    if (watch != WatchState::Disconnected || !advertisingRetry.due(now) ||
        (advertising && advertising->isAdvertising())) {
        return;
    }
    if ((advertising || configureBle()) && advertising->start()) {
        advertisingStarted = true;
        advertisingRetry.started();
        Serial.println("RSC 0x1814 advertising");
    } else {
        advertisingRetry.failed(nowMs());
        Serial.println("RSC advertising failed, retrying in 5 s");
    }
}

void logWatchEvent(WatchState watch) {
    static auto previous = WatchState::Disconnected;
    if (watch != previous) {
        switch (watch) {
            case WatchState::Disconnected: Serial.println("Watch disconnected"); break;
            case WatchState::Connected:
                Serial.println(previous == WatchState::Subscribed ? "Watch unsubscribed" : "Watch connected");
                break;
            case WatchState::Subscribed: Serial.println("Watch subscribed to 0x2A53"); break;
        }
        previous = watch;
    }
}

void runTick(TimeMs now, bool subscribed) {
    auto action = bridge.tick(now, subscribed, r2.takeScanResult());
    if (action == Action::Poll) {
        action = r2.poll(bridge);
    }
    switch (action) {
        case Action::StartScan: r2.startScan(); break;
        case Action::CancelScan: Serial.println("R2 scan cancelled (watch absent)"); r2.close(); break;
        case Action::ReleaseForWatch: Serial.println("R2 released (watch saved)"); r2.close(); break;
        case Action::ReleaseForIdle: Serial.println("R2 released (3 min idle, KS Fit free)"); r2.close(); break;
        case Action::ReleaseForR2Drop: Serial.println("R2 disconnected or status unavailable"); r2.close(); break;
        case Action::None:
        case Action::Poll:
            break;
    }
    if (subscribed) {
        watchPeers.notify(bridge.measurement());
    }
}

const char* r2Label(R2State state) {
    switch (state) {
        case R2State::WaitingForWatch: return "idle";
        case R2State::Scanning: return "scanning/connecting";
        case R2State::NotFound: return "not found";
        case R2State::ConnectedIdle: return "connected, belt idle";
        case R2State::Running: return "streaming";
        case R2State::ReleasedForKsFit: return "released for KS Fit";
    }
    return "?";
}

void sendStatus(WebServer& server) {
    StatusReport report{};
    xQueuePeek(statusReports, &report, 0);

    char localTime[28] = "null";
    if (timeSynced.load()) {
        const auto now = std::time(nullptr);
        std::tm local{};
        char iso[25];
        if (localtime_r(&now, &local) && std::strftime(iso, sizeof(iso), "%Y-%m-%dT%H:%M:%S%z", &local) == 24) {
            std::snprintf(localTime, sizeof(localTime), "\"%.22s:%s\"", iso, iso + 22);
        }
    }

    char belt[5] = "null";
    if (report.bridge.beltState) {
        std::snprintf(belt, sizeof(belt), "%u", static_cast<unsigned>(*report.bridge.beltState));
    }
    char json[512];
    std::snprintf(json, sizeof(json),
                  "{\"uptime_ms\":%llu,\"time\":%s,\"wifi_rssi\":%ld,"
                  "\"watch\":{\"subscribed\":%s,\"subscribed_handles\":%u},"
                  "\"r2_state\":\"%s\",\"belt_state\":%s,\"kmh\":%.1f,\"spm\":%d,\"steps\":%d,"
                  "\"watch_connects\":%u,\"watch_disconnects\":%u,\"r2_connects\":%u,"
                  "\"r2_drops\":%u,\"r2_status_timeouts\":%u}",
                  static_cast<unsigned long long>(nowMs()), localTime, static_cast<long>(WiFi.RSSI()),
                  report.subscribedHandles ? "true" : "false", report.subscribedHandles,
                  r2Label(report.bridge.r2), belt, report.bridge.speedKmh, report.bridge.cadenceSpm,
                  report.bridge.steps, report.watchConnects, report.watchDisconnects, report.r2Connects,
                  report.r2Drops, report.r2StatusTimeouts);
    server.send(200, "application/json", json);
}

void onWifiEvent(WiFiEvent_t event, WiFiEventInfo_t info) {
    static IPAddress lastIp;
    if (event == ARDUINO_EVENT_WIFI_STA_GOT_IP) {
        lastIp = IPAddress(info.got_ip.ip_info.ip.addr);
        wifiUp = true;
        Serial.printf("Wi-Fi connected, IP %s\n", lastIp.toString().c_str());
    } else if ((event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED || event == ARDUINO_EVENT_WIFI_STA_LOST_IP) &&
               wifiUp.exchange(false)) {
        Serial.printf("Wi-Fi disconnected, last IP %s\n", lastIp.toString().c_str());
    }
}

void wifiTask(void*) {
    WiFi.onEvent(onWifiEvent);
    WiFi.mode(WIFI_STA);
    WiFi.setAutoReconnect(true);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

    while (!wifiUp.load()) {
        vTaskDelay(pdMS_TO_TICKS(100));
    }

    sntp_set_time_sync_notification_cb([](timeval*) {
        if (!timeSynced.exchange(true)) {
            Serial.println("NTP synced");
        }
    });
    configTzTime(kKyivTimezone, "pool.ntp.org");
    if (MDNS.begin(kHostname)) {
        MDNS.addService("http", "tcp", 80);
    } else {
        Serial.println("mDNS failed to start");
    }

    WebServer server(80);
    server.on("/status", HTTP_GET, [&server] { sendStatus(server); });
    server.begin();
    for (;;) {
        if (wifiUp.load()) {
            server.handleClient();
        }
        vTaskDelay(pdMS_TO_TICKS(20));
    }
}
}  // namespace

void setup() {
    Serial.begin(115200);
    if (!r2.allocate() || !watchPeers.allocate()) {
        advertisingRetry.failed(nowMs(), false);
        Serial.println("Could not allocate BLE queues or discovery task");
    }
    // WebServer can wait on a slow client. Keep that work off the BLE bridge loop.
    statusReports = xQueueCreate(1, sizeof(StatusReport));
    if (!statusReports || xTaskCreate(wifiTask, "Wi-Fi HTTP", 6144, nullptr, 1, nullptr) != pdPASS) {
        Serial.println("Could not allocate Wi-Fi status queue or task");
    }
}

void loop() {
    static TimeMs nextTick = nowMs();
    static bool previousSubscribed = false;
    const auto watch = watchPeers.state();
    const bool subscribed = watch == WatchState::Subscribed;
    logWatchEvent(watch);
    ensureAdvertising(nowMs(), watch);

    if (advertisingStarted) {
        r2.drainPackets(bridge);
    }
    const auto now = nowMs();
    if (now >= nextTick || subscribed != previousSubscribed) {
        nextTick = now + kTickIntervalMs;
        previousSubscribed = subscribed;
        if (advertisingStarted) {
            runTick(now, subscribed);
        }
        const auto& status = bridge.snapshot();
        if (statusReports) {
            const StatusReport report{status, watchPeers.subscribedHandles(), watchPeers.connects(),
                                      watchPeers.disconnects(), r2.connects(), r2.drops(), bridge.statusTimeouts()};
            xQueueOverwrite(statusReports, &report);
        }
        char belt[4] = "-";
        if (status.beltState) {
            std::snprintf(belt, sizeof(belt), "%u", static_cast<unsigned>(*status.beltState));
        }
        Serial.printf("Watch %s | R2 %s | belt %s | %.1f km/h | %d spm | %d steps | Wi-Fi %s\n",
                      subscribed ? "on" : "off", r2Label(status.r2), belt,
                      status.speedKmh, status.cadenceSpm, status.steps, wifiUp.load() ? "up" : "down");
    }
    if (advertisingStarted) {
        r2.service();
    }
    delay(5);
}
