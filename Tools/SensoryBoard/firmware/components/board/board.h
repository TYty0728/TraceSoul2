#pragma once
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"
void board_init();
esp_err_t board_frame(const uint8_t *rgb565);
esp_err_t board_audio(const uint8_t *pcm, size_t size);
esp_err_t board_record(uint8_t *pcm, size_t size);
esp_err_t board_record_begin(uint8_t *scratch);
esp_err_t board_record_read(uint8_t *pcm, size_t size);
void board_touch_status(char *out, size_t size);
void board_touch_test(bool enabled);
struct BoardTouch { bool ready, down, testing; uint16_t x, y; uint32_t count; };
BoardTouch board_touch_snapshot();
void board_mic_pause(bool paused);
bool board_mic_paused();
void board_brightness(unsigned percent);
void board_stop_playback();
void board_allow_playback();
