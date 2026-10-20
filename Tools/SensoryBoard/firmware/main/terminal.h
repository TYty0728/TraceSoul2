#pragma once
#include "esp_tls.h"
#include "freertos/FreeRTOS.h"
struct TerminalLink {
    esp_tls_t *tls = nullptr;
    bool closed = false;
    int read(void *data, size_t size, TickType_t wait);
    int write(const void *data, size_t size, TickType_t wait);
};
void terminal_run(TerminalLink &link);
void network_init();
bool network_save(const char *data, size_t size);
void network_status(char *out, size_t size);
