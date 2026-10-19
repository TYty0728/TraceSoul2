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
