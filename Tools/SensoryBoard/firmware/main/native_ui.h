#pragma once
#include <stdint.h>
#include "esp_err.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
void native_ui_init(SemaphoreHandle_t output_lock);
esp_err_t native_ui_frame(const uint8_t *pixels);
void native_ui_command();
void native_ui_status(char *out,size_t size);
