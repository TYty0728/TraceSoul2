#include <cstdio>
#include <cstring>
#include <cmath>
#include "board.h"
#include "cJSON.h"
#include "driver/usb_serial_jtag.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "splash.h"

static constexpr size_t FRAME_BYTES = 320 * 480 * 2;
static constexpr size_t MAX_PAYLOAD = 48000 * 2 * 10;
static constexpr size_t CHUNK = 4096;
static uint32_t frames = 0, plays = 0;
static uint32_t checksum(const uint8_t *p, size_t n) {
    uint32_t hash = 2166136261u;
    for (size_t i = 0; i < n; ++i) hash = (hash ^ p[i]) * 16777619u;
    return hash;
}
static void reply(uint32_t id, const char *status, size_t received = 0, const char *error = "") {
    char line[320];
    int n = snprintf(line, sizeof(line), "{\"protocol\":1,\"firmware\":\"0.3.0\",\"id\":%lu,\"status\":\"%s\",\"received\":%u,\"frames\":%lu,\"plays\":%lu,\"uptime_ms\":%llu,\"error\":\"%s\"}\n",
        (unsigned long)id, status, (unsigned)received, (unsigned long)frames, (unsigned long)plays,
        (unsigned long long)(esp_timer_get_time() / 1000), error);
    usb_serial_jtag_write_bytes(line, n, pdMS_TO_TICKS(1000));
}
static bool read_exact(uint8_t *out, size_t size) {
    size_t received = 0;
    int64_t deadline = esp_timer_get_time() + 5000000;
    while (received < size && esp_timer_get_time() < deadline) {
        int n = usb_serial_jtag_read_bytes(out + received, size - received, pdMS_TO_TICKS(50));
        if (n > 0) received += n;
    }
    return received == size;
}
static void drain() {
    uint8_t bytes[256];
    while (usb_serial_jtag_read_bytes(bytes, sizeof(bytes), pdMS_TO_TICKS(100)) > 0) {}
}
static bool uint_field(cJSON *json, const char *name, uint32_t *out) {
    cJSON *item = cJSON_GetObjectItemCaseSensitive(json, name);
    if (!cJSON_IsNumber(item) || !std::isfinite(item->valuedouble) || item->valuedouble < 0 ||
        item->valuedouble > 4294967295.0 || floor(item->valuedouble) != item->valuedouble) return false;
    *out = (uint32_t)item->valuedouble;
    return true;
}
static bool send_recording(uint32_t id, const uint8_t *pcm, size_t size) {
    char line[256];
    int length = snprintf(line, sizeof(line), "{\"protocol\":1,\"id\":%lu,\"status\":\"captured\",\"size\":%u,\"sample_rate\":48000,\"checksum\":%lu}\n",
        (unsigned long)id, (unsigned)size, (unsigned long)checksum(pcm, size));
    if (usb_serial_jtag_write_bytes(line, length, pdMS_TO_TICKS(1000)) != length) return false;
    for (size_t offset = 0; offset < size; offset += CHUNK) {
        size_t n = size - offset < CHUNK ? size - offset : CHUNK;
        if (usb_serial_jtag_write_bytes(pcm + offset, n, pdMS_TO_TICKS(2000)) != (int)n) return false;
        size_t count = 0; bool complete = false;
        while (count + 1 < sizeof(line)) {
            uint8_t c;
            if (usb_serial_jtag_read_bytes(&c, 1, pdMS_TO_TICKS(2000)) != 1) break;
            if (c == '\n') { complete = true; break; }
            line[count++] = c;
        }
        line[count] = 0;
        cJSON *ack = complete ? cJSON_Parse(line) : nullptr;
        uint32_t ack_id = 0, received = 0;
        bool valid = ack && uint_field(ack, "id", &ack_id) && ack_id == id &&
            uint_field(ack, "ack", &received) && received == offset + n;
        cJSON_Delete(ack);
        if (!valid) return false;
    }
    return true;
}
static bool stream_microphone(uint32_t id, uint8_t *pcm, size_t *received) {
    constexpr size_t block_size = 3072; // 32ms at 48kHz; one 512-sample VAD window at 16kHz.
    if (board_record_begin(pcm) != ESP_OK) return false;
    reply(id, "ready");
    const int64_t deadline = esp_timer_get_time() + 30000000;
    char line[256];
    for (unsigned int block = 0; block < 625 && esp_timer_get_time() < deadline; ++block) {
        if (board_record_read(pcm, block_size) != ESP_OK) return false;
        *received += block_size;
        int length = snprintf(line, sizeof(line), "{\"protocol\":1,\"id\":%lu,\"status\":\"samples\",\"received\":%u,\"size\":3072,\"sample_rate\":48000,\"checksum\":%lu}\n",
            (unsigned long)id, (unsigned)*received, (unsigned long)checksum(pcm, block_size));
        if (usb_serial_jtag_write_bytes(line, length, pdMS_TO_TICKS(1000)) != length ||
            usb_serial_jtag_write_bytes(pcm, block_size, pdMS_TO_TICKS(1000)) != (int)block_size) return false;
        size_t count = 0; bool complete = false;
        const int64_t ack_deadline = esp_timer_get_time() + 2000000;
        while (count + 1 < sizeof(line) && esp_timer_get_time() < ack_deadline) {
            uint8_t c;
            if (usb_serial_jtag_read_bytes(&c, 1, pdMS_TO_TICKS(20)) != 1) continue;
            if (c == '\n') { complete = true; break; }
            line[count++] = c;
        }
        line[count] = 0;
        cJSON *ack = complete ? cJSON_Parse(line) : nullptr;
        uint32_t ack_id = 0, total = 0;
        cJSON *stop = ack ? cJSON_GetObjectItemCaseSensitive(ack, "stop") : nullptr;
        bool valid = ack && uint_field(ack, "id", &ack_id) && ack_id == id &&
            uint_field(ack, "ack", &total) && total == *received && cJSON_IsBool(stop);
        bool stopped = cJSON_IsTrue(stop);
        cJSON_Delete(ack);
        if (!valid) return false;
        if (stopped) return true;
    }
    return false; // A limit is not a successful final utterance.
}
extern "C" void app_main() {
    board_init();
    auto *payload = (uint8_t *)heap_caps_malloc(MAX_PAYLOAD, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    assert(payload);
    size_t offset = 0;
    for (size_t i = 0; i < sizeof(SPLASH_RLE) / sizeof(SPLASH_RLE[0]); i += 2) {
        for (uint32_t n = 0; n < SPLASH_RLE[i]; n++) {
            assert(offset + 2 <= FRAME_BYTES);
            payload[offset++] = SPLASH_RLE[i + 1] >> 8;
            payload[offset++] = SPLASH_RLE[i + 1] & 0xff;
        }
    }
    assert(offset == FRAME_BYTES);
    esp_log_level_set("*", ESP_LOG_NONE);
    // A recorded-audio chunk must fit the TX byte ring in one write.
    usb_serial_jtag_driver_config_t usb = {.tx_buffer_size = 8192, .rx_buffer_size = 8192};
    ESP_ERROR_CHECK(usb_serial_jtag_driver_install(&usb));
    ESP_ERROR_CHECK(board_frame(payload));
    reply(0, "boot");
    while (true) {
        char line[512]; size_t count = 0;
        bool complete = false;
        while (count + 1 < sizeof(line)) {
            uint8_t c;
            if (usb_serial_jtag_read_bytes(&c, 1, pdMS_TO_TICKS(count ? 2000 : 100)) != 1) {
                if (count) break;
                continue;
            }
            if (c == '\n') { complete = true; break; }
            line[count++] = c;
        }
        line[count] = 0;
        if (!complete) { drain(); reply(0, "error", 0, "header_timeout_or_limit"); continue; }
        cJSON *json = cJSON_Parse(line);
        uint32_t id = 0, size = 0, expected = 0;
        cJSON *op = json ? cJSON_GetObjectItemCaseSensitive(json, "op") : nullptr;
        if (!json || !uint_field(json, "id", &id) || id == 0 || !cJSON_IsString(op)) {
            cJSON_Delete(json); reply(id, "error", 0, "bad_header"); continue;
        }
        bool hello = !strcmp(op->valuestring, "hello");
        bool frame = !strcmp(op->valuestring, "frame");
        bool audio = !strcmp(op->valuestring, "audio");
        if (hello) { cJSON_Delete(json); reply(id, "ready"); continue; }
        if (!strcmp(op->valuestring, "listen")) {
            cJSON_Delete(json);
            size_t received = 0;
            if (!stream_microphone(id, payload, &received)) {
                drain(); reply(id, "error", received, "microphone_stream_failed"); continue;
            }
            reply(id, "done", received);
            continue;
        }
        if (!strcmp(op->valuestring, "record")) {
            uint32_t duration_ms = 0;
            bool valid = uint_field(json, "duration_ms", &duration_ms) && duration_ms >= 1000 && duration_ms <= 10000;
            cJSON_Delete(json);
            if (!valid) { reply(id, "error", 0, "bad_record_duration"); continue; }
            size = duration_ms * 96; // PCM16 mono, 48 kHz.
            reply(id, "ready");
            if (board_record(payload, size) != ESP_OK) { reply(id, "error", 0, "record_failed"); continue; }
            if (!send_recording(id, payload, size)) { drain(); reply(id, "error", 0, "record_transfer_failed"); continue; }
            reply(id, "done", size);
            continue;
        }
        bool valid = uint_field(json, "size", &size) && uint_field(json, "checksum", &expected) &&
            ((frame && size == FRAME_BYTES) || (audio && size > 0 && size <= MAX_PAYLOAD && size % 2 == 0));
        cJSON_Delete(json);
        if (!valid) { reply(id, "error", 0, "bad_payload_spec"); continue; }
        reply(id, "ready");
        size_t received = 0;
        while (received < size) {
            size_t n = size - received < CHUNK ? size - received : CHUNK;
            if (!read_exact(payload + received, n)) break;
            received += n;
            reply(id, "chunk", received);
        }
        if (received != size) { drain(); reply(id, "error", received, "payload_timeout"); continue; }
        if (checksum(payload, size) != expected) { reply(id, "error", received, "checksum_mismatch"); continue; }
        esp_err_t err = frame ? board_frame(payload) : board_audio(payload, size);
        if (err != ESP_OK) { reply(id, "error", received, "output_failed"); continue; }
        if (frame) frames++; else plays++;
        reply(id, "done", received);
    }
}
