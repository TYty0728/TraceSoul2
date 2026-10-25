#include <cstdio>
#include <cstring>
#include <cmath>
#include <atomic>
#include "board.h"
#include "cJSON.h"
#include "driver/usb_serial_jtag.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "native_ui.h"
#include "device_control.h"
#include "terminal.h"
#include "esp_system.h"
#include "freertos/semphr.h"
#include "freertos/stream_buffer.h"

static SemaphoreHandle_t output_lock;
struct OutputGuard {
    bool held;
    OutputGuard():held(xSemaphoreTake(output_lock,pdMS_TO_TICKS(1000))==pdTRUE){}
    ~OutputGuard(){if(held)xSemaphoreGive(output_lock);}
};

static constexpr size_t FRAME_BYTES = 320 * 480 * 2;
static constexpr size_t MAX_PAYLOAD = 48000 * 2 * 10;
static constexpr size_t CHUNK = 4096;
static uint32_t frames = 0, plays = 0;
static uint32_t checksum(const uint8_t *p, size_t n) {
    uint32_t hash = 2166136261u;
    for (size_t i = 0; i < n; ++i) hash = (hash ^ p[i]) * 16777619u;
    return hash;
}
static void reply(TerminalLink &link, uint32_t id, const char *status, size_t received = 0, const char *error = "") {
    char line[1300];
    char state[360],touch_state[180],ui_state[400]; network_status(state,sizeof(state));board_touch_status(touch_state,sizeof(touch_state));native_ui_status(ui_state,sizeof(ui_state));
    int n = snprintf(line, sizeof(line), "{\"protocol\":1,\"firmware\":\"0.4.8\",\"audio_stream_version\":1,\"id\":%lu,\"status\":\"%s\",\"received\":%u,\"frames\":%lu,\"plays\":%lu,\"uptime_ms\":%llu,\"error\":\"%s\",\"local_mic_paused\":%s,%s,%s,%s}\n",
        (unsigned long)id, status, (unsigned)received, (unsigned long)frames, (unsigned long)plays,
        (unsigned long long)(esp_timer_get_time() / 1000), error, board_mic_paused()?"true":"false", state, touch_state,ui_state);
    if(n<0||n>=(int)sizeof(line)){link.closed=true;return;}
    link.write(line, n, pdMS_TO_TICKS(1000));
}
static bool read_exact(TerminalLink &link, uint8_t *out, size_t size) {
    size_t received = 0;
    int64_t deadline = esp_timer_get_time() + 5000000;
    while (received < size && esp_timer_get_time() < deadline) {
        int n = link.read(out + received, size - received, pdMS_TO_TICKS(50));
        if (n > 0) received += n;
    }
    return received == size;
}
static void drain(TerminalLink &link) {
    uint8_t bytes[256];
    while (link.read(bytes, sizeof(bytes), pdMS_TO_TICKS(100)) > 0) {}
}
static bool uint_field(cJSON *json, const char *name, uint32_t *out) {
    cJSON *item = cJSON_GetObjectItemCaseSensitive(json, name);
    if (!cJSON_IsNumber(item) || !std::isfinite(item->valuedouble) || item->valuedouble < 0 ||
        item->valuedouble > 4294967295.0 || floor(item->valuedouble) != item->valuedouble) return false;
    *out = (uint32_t)item->valuedouble;
    return true;
}
static bool send_recording(TerminalLink &link, uint32_t id, const uint8_t *pcm, size_t size) {
    char line[256];
    int length = snprintf(line, sizeof(line), "{\"protocol\":1,\"id\":%lu,\"status\":\"captured\",\"size\":%u,\"sample_rate\":48000,\"checksum\":%lu}\n",
        (unsigned long)id, (unsigned)size, (unsigned long)checksum(pcm, size));
    if (link.write(line, length, pdMS_TO_TICKS(1000)) != length) return false;
    for (size_t offset = 0; offset < size; offset += CHUNK) {
        size_t n = size - offset < CHUNK ? size - offset : CHUNK;
        if (link.write(pcm + offset, n, pdMS_TO_TICKS(2000)) != (int)n) return false;
        size_t count = 0; bool complete = false;
        while (count + 1 < sizeof(line)) {
            uint8_t c;
            if (link.read(&c, 1, pdMS_TO_TICKS(2000)) != 1) break;
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
static bool stream_microphone(TerminalLink &link, uint32_t id, uint8_t *pcm, size_t *received) {
    constexpr size_t block_size = 3072; // 32ms at 48kHz; one 512-sample VAD window at 16kHz.
    if (board_record_begin(pcm) != ESP_OK) return false;
    reply(link, id, "ready");
    const int64_t deadline = esp_timer_get_time() + 30000000;
    char line[256];
    for (unsigned int block = 0; block < 625 && esp_timer_get_time() < deadline; ++block) {
        if (board_record_read(pcm, block_size) != ESP_OK) return false;
        *received += block_size;
        int length = snprintf(line, sizeof(line), "{\"protocol\":1,\"id\":%lu,\"status\":\"samples\",\"received\":%u,\"size\":3072,\"sample_rate\":48000,\"checksum\":%lu}\n",
            (unsigned long)id, (unsigned)*received, (unsigned long)checksum(pcm, block_size));
        if (link.write(line, length, pdMS_TO_TICKS(1000)) != length ||
            link.write(pcm, block_size, pdMS_TO_TICKS(1000)) != (int)block_size) return false;
        size_t count = 0; bool complete = false;
        const int64_t ack_deadline = esp_timer_get_time() + 2000000;
        while (count + 1 < sizeof(line) && esp_timer_get_time() < ack_deadline) {
            uint8_t c;
            if (link.read(&c, 1, pdMS_TO_TICKS(20)) != 1) continue;
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
        bool speech = cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(ack,"speech"));
        cJSON_Delete(ack);
        if (!valid) return false;
        if(speech)native_ui_speech();
        if (stopped) return true;
    }
    return false; // A limit is not a successful final utterance.
}
struct AudioStream {
    StreamBufferHandle_t buffer;
    SemaphoreHandle_t done;
    size_t size;
    std::atomic<bool> abort{false}, failed{false};
};
static void play_stream(void *argument) {
    auto *stream=static_cast<AudioStream *>(argument);
    uint8_t block[CHUNK]; unsigned epoch=0;
    bool ok=board_audio_begin(&epoch)==ESP_OK;
    size_t played=0;
    while(ok && played<stream->size && !stream->abort.load()) {
        size_t want=stream->size-played<CHUNK?stream->size-played:CHUNK;
        size_t n=xStreamBufferReceive(stream->buffer,block,want,pdMS_TO_TICKS(1000));
        ok=n>0 && n%2==0 && board_audio_write(block,n,epoch)==ESP_OK;
        played+=n;
    }
    ok=ok && !stream->abort.load() && played==stream->size;
    if(board_audio_end(ok)!=ESP_OK)ok=false;
    stream->failed.store(!ok);
    xSemaphoreGive(stream->done);
    vTaskDelete(nullptr);
}
// One playback transaction, bounded ring buffer, no page or 8-second restarts.
// Each wire packet is checksummed before it can reach the speaker.
static void stream_audio(TerminalLink &link,uint32_t id,uint8_t *buffer,size_t size,uint32_t expected) {
    StaticStreamBuffer_t storage;
    AudioStream stream;
    stream.size=size;
    stream.buffer=xStreamBufferCreateStatic(MAX_PAYLOAD,1,buffer,&storage);
    stream.done=xSemaphoreCreateBinary();
    auto *block=static_cast<uint8_t *>(heap_caps_malloc(CHUNK+4,MALLOC_CAP_8BIT));
    if(!stream.buffer || !stream.done || !block) {
        if(stream.buffer)vStreamBufferDelete(stream.buffer);
        if(stream.done)vSemaphoreDelete(stream.done);
        free(block); reply(link,id,"error",0,"audio_memory_unavailable"); return;
    }
    reply(link,id,"ready");
    size_t received=0;uint32_t hash=2166136261u;bool started=false;
    const char *error="";
    while(received<size) {
        size_t n=size-received<CHUNK?size-received:CHUNK;
        if(!read_exact(link,block,n+4)){error="audio_transfer_timeout";break;}
        uint32_t packet=(uint32_t)block[n]|((uint32_t)block[n+1]<<8)|((uint32_t)block[n+2]<<16)|((uint32_t)block[n+3]<<24);
        if(checksum(block,n)!=packet){error="audio_packet_checksum";break;}
        for(size_t i=0;i<n;i++)hash=(hash^block[i])*16777619u;
        if(received+n==size && hash!=expected){error="checksum_mismatch";break;}
        size_t sent=0;int64_t deadline=esp_timer_get_time()+5000000;
        while(xStreamBufferSpacesAvailable(stream.buffer)<n && !stream.failed.load() && esp_timer_get_time()<deadline)
            vTaskDelay(pdMS_TO_TICKS(10));
        if(xStreamBufferSpacesAvailable(stream.buffer)>=n && !stream.failed.load())
            sent=xStreamBufferSend(stream.buffer,block,n,0);
        if(sent!=n || stream.failed.load()){error="audio_stream_stopped";break;}
        received+=n;
        // Prefill about 1.4 seconds. The ring absorbs transport jitter thereafter.
        if(!started && (received>=131072 || received==size)) {
            started=xTaskCreate(play_stream,"audio-output",8192,&stream,5,nullptr)==pdPASS;
            if(!started){error="audio_task_unavailable";break;}
        }
        reply(link,id,"chunk",received);
    }
    if(*error)stream.abort.store(true);
    if(started) {
        // At most the bounded 10-second ring plus drain remains after upload.
        xSemaphoreTake(stream.done,portMAX_DELAY);
        if(!*error && stream.failed.load())error="audio_stream_stopped";
    }
    vStreamBufferDelete(stream.buffer);vSemaphoreDelete(stream.done);free(block);
    if(*error) {reply(link,id,"error",received,error);link.closed=link.tls!=nullptr;}
    else {plays++;reply(link,id,"done",received);}
}
void terminal_run(TerminalLink &link) {
    auto *payload=(uint8_t *)heap_caps_malloc(MAX_PAYLOAD,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    if(!payload){link.closed=true;return;}
    while (!link.closed) {
        char line[512]; size_t count = 0;
        bool complete = false;
        int64_t idle_deadline=esp_timer_get_time()+30000000;
        int64_t header_deadline=0;
        while (count + 1 < sizeof(line)) {
            uint8_t c;
            if (link.read(&c, 1, pdMS_TO_TICKS(count ? 2000 : 100)) != 1) {
                if (link.closed || (link.tls && esp_timer_get_time()>idle_deadline)) { link.closed=true; break; }
                if (link.tls && count && esp_timer_get_time()<header_deadline) continue;
                if (count) break;
                continue;
            }
            if (c == '\n') { complete = true; break; }
            if (!count) header_deadline=esp_timer_get_time()+2000000;
            line[count++] = c;
        }
        line[count] = 0;
        if (link.closed) break;
        if (!complete) { drain(link); reply(link, 0, "error", 0, "header_timeout_or_limit"); continue; }
        cJSON *json = cJSON_Parse(line);
        uint32_t id = 0, size = 0, expected = 0;
        cJSON *op = json ? cJSON_GetObjectItemCaseSensitive(json, "op") : nullptr;
        if (!json || !uint_field(json, "id", &id) || id == 0 || !cJSON_IsString(op)) {
            cJSON_Delete(json); reply(link, id, "error", 0, "bad_header"); continue;
        }
        OutputGuard guard;
        if (!guard.held) { cJSON_Delete(json); reply(link,id,"error",0,"device_busy"); continue; }
        native_ui_command();
        if(!strcmp(op->valuestring,"control")){
            uint32_t ack=0;
            auto *controller=cJSON_GetObjectItemCaseSensitive(json,"controller");
            auto *active=cJSON_GetObjectItemCaseSensitive(json,"active");
            auto *allowed=cJSON_GetObjectItemCaseSensitive(json,"allowed");
            auto *failed=cJSON_GetObjectItemCaseSensitive(json,"failed");
            auto *phase=cJSON_GetObjectItemCaseSensitive(json,"phase");
            bool valid=cJSON_IsString(controller)&&strlen(controller->valuestring)==32&&
                strspn(controller->valuestring,"0123456789abcdef")==32&&uint_field(json,"ack",&ack)&&
                cJSON_IsBool(active)&&cJSON_IsBool(allowed)&&cJSON_IsBool(failed)&&cJSON_IsString(phase);
            unsigned index=0;const char *names[]={"standby","focused","listening","thinking","speaking","paused","offline","error"};
            if(valid){for(;index<8;index++)if(!strcmp(phase->valuestring,names[index]))break;valid=index<8;}
            bool accepted=valid&&device_control_update(controller->valuestring,ack,cJSON_IsTrue(active),cJSON_IsTrue(allowed),index,cJSON_IsTrue(failed));
            cJSON_Delete(json);
            if(!accepted){reply(link,id,"error",0,"control_rejected");continue;}
            char response[420];device_control_receipt(response,sizeof(response),id);link.write(response,strlen(response),pdMS_TO_TICKS(1000));continue;
        }
        bool hello = !strcmp(op->valuestring, "hello");
        bool frame = !strcmp(op->valuestring, "frame");
        bool scene = !strcmp(op->valuestring, "scene");
        bool audio = !strcmp(op->valuestring, "audio");
        bool setup = !link.tls && !strcmp(op->valuestring,"wifi_setup");
        if(!strcmp(op->valuestring,"touch_test") || !strcmp(op->valuestring,"touch_stop")){
            board_touch_test(!strcmp(op->valuestring,"touch_test"));cJSON_Delete(json);reply(link,id,"ready");continue;
        }
        if (!link.tls && !strcmp(op->valuestring,"reboot")) { cJSON_Delete(json); reply(link,id,"ready"); vTaskDelay(pdMS_TO_TICKS(200)); esp_restart(); }
        if (hello) { cJSON_Delete(json); reply(link, id, "ready"); continue; }
        if(!strcmp(op->valuestring,"ui")){
            uint32_t phase=0,focus=0,countdown=0;
            bool valid=uint_field(json,"phase",&phase)&&phase<=7&&uint_field(json,"focus_ms",&focus)&&focus<=30000&&
                uint_field(json,"countdown",&countdown)&&countdown<=10;
            cJSON_Delete(json);
            if(!valid){reply(link,id,"error",0,"bad_ui_state");continue;}
            native_ui_phase(phase,focus);
            reply(link,id,"ready");
            if(countdown){
                native_ui_countdown(countdown);
                int64_t until=esp_timer_get_time()+int64_t(countdown)*1000000;
                while(esp_timer_get_time()<until&&!board_mic_paused())vTaskDelay(pdMS_TO_TICKS(20));
                native_ui_countdown(0);
            }
            reply(link,id,"done");continue;
        }
        if (!strcmp(op->valuestring,"audio_stream")) {
            bool valid=uint_field(json,"size",&size) && size>0 && size<=48000*2*120 && size%2==0 &&
                uint_field(json,"checksum",&expected);
            cJSON_Delete(json);
            if(!valid){reply(link,id,"error",0,"bad_payload_spec");continue;}
            stream_audio(link,id,payload,size,expected);continue;
        }
        if (!strcmp(op->valuestring, "listen")) {
            auto *notice=cJSON_GetObjectItemCaseSensitive(json,"notice");
            bool valid_notice=!notice||(cJSON_IsString(notice)&&(!strcmp(notice->valuestring,"")||
                !strcmp(notice->valuestring,"speaker_unconfirmed")||!strcmp(notice->valuestring,"no_speech")));
            unsigned notice_index=cJSON_IsString(notice)&&!strcmp(notice->valuestring,"speaker_unconfirmed")?1:
                cJSON_IsString(notice)&&!strcmp(notice->valuestring,"no_speech")?2:0;
            cJSON_Delete(json);
            if(!valid_notice){reply(link,id,"error",0,"bad_listen_notice");continue;}
            if(board_mic_paused()){reply(link,id,"error",0,"local_mic_paused");continue;}
            size_t received = 0;
            native_ui_capture(true,notice_index);
            bool complete=stream_microphone(link, id, payload, &received);
            native_ui_capture(false);
            if (!complete) {
                // A local stop has no pending payload to recover. Reply before any
                // blocking TLS drain so the bridge can observe the cancellation.
                if(board_mic_paused()) reply(link, id, "error", received, "local_mic_paused");
                else {drain(link); reply(link, id, "error", received, "microphone_stream_failed");}
                continue;
            }
            reply(link, id, "done", received);
            continue;
        }
        if (!strcmp(op->valuestring, "record")) {
            uint32_t duration_ms = 0;
            bool valid = uint_field(json, "duration_ms", &duration_ms) && duration_ms >= 1000 && duration_ms <= 10000;
            cJSON_Delete(json);
            if (!valid) { reply(link, id, "error", 0, "bad_record_duration"); continue; }
            if(board_mic_paused()){reply(link,id,"error",0,"local_mic_paused");continue;}
            size = duration_ms * 96; // PCM16 mono, 48 kHz.
            reply(link, id, "ready");
            native_ui_capture(true);
            esp_err_t captured=board_record(payload,size);
            native_ui_capture(false);
            if (captured != ESP_OK) { reply(link, id, "error", 0, "record_failed"); continue; }
            if (!send_recording(link, id, payload, size)) { drain(link); reply(link, id, "error", 0, "record_transfer_failed"); continue; }
            reply(link, id, "done", size);
            continue;
        }
        bool valid = uint_field(json, "size", &size) && uint_field(json, "checksum", &expected) &&
            ((setup && size > 0 && size <= 7000) || (scene && size>=12 && size<=MAX_PAYLOAD) || (frame && size == FRAME_BYTES) || (audio && size > 0 && size <= MAX_PAYLOAD && size % 2 == 0));
        auto *art=cJSON_GetObjectItemCaseSensitive(json,"artwork");
        bool artwork=(frame||scene)&&cJSON_IsTrue(art);
        if(art&&(!(frame||scene)||!cJSON_IsBool(art)))valid=false;
        cJSON_Delete(json);
        if (!valid) { reply(link, id, "error", 0, "bad_payload_spec"); continue; }
        reply(link, id, "ready");
        size_t received = 0;
        while (received < size) {
            size_t n = size - received < CHUNK ? size - received : CHUNK;
            if (!read_exact(link, payload + received, n)) break;
            received += n;
            reply(link, id, "chunk", received);
        }
        if (received != size) { drain(link); reply(link, id, "error", received, "payload_timeout"); continue; }
        if (checksum(payload, size) != expected) { reply(link, id, "error", received, "checksum_mismatch"); continue; }
        if (setup) {
            bool saved=network_save((const char *)payload,size);
            memset(payload,0,size);
            reply(link,id,saved?"done":"error",received,saved?"":"wifi_config_invalid"); continue;
        }
        esp_err_t err = scene ? native_ui_scene(payload,size,artwork) : frame ? native_ui_frame(payload,artwork) : board_audio(payload, size);
        if (err != ESP_OK) { reply(link, id, "error", received, "output_failed"); continue; }
        if (frame||scene) frames++; else plays++;
        reply(link, id, "done", received);
    }
    heap_caps_free(payload);
}

extern "C" void app_main() {
    board_init();
    esp_log_level_set("*", ESP_LOG_NONE);
    // A recorded-audio chunk must fit the TX byte ring in one write.
    usb_serial_jtag_driver_config_t usb = {.tx_buffer_size = 8192, .rx_buffer_size = 8192};
    ESP_ERROR_CHECK(usb_serial_jtag_driver_install(&usb));
    output_lock=xSemaphoreCreateMutex();
    assert(output_lock);
    device_control_init();
    board_mic_pause(true); // Restore local privacy preferences before accepting any capture.
    network_init();
    native_ui_init(output_lock);
    TerminalLink link;
    reply(link,0,"boot");
    terminal_run(link);
}
