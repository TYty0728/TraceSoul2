# Reused board support

Hardware source: [Waveshare ESP32-S3-Touch-LCD-3.5](https://github.com/waveshareteam/ESP32-S3-Touch-LCD-3.5), `ESP-IDF/03_lvgl_system` subtree `7b9104d4a0c1ae37e90d775949217387d776cafc`, retrieved 2026-10-14.

- `firmware/components/board/esp_axp2101_port.*` and `esp_es8311_port.cpp` originate from Waveshare's `components/esp_port`. Audio error handling and idle DMA clearing are adapted for this terminal. `board.cpp` adapts its SPI LCD, backlight and I2C wiring, with EXIO7 amplifier control from the official schematic. Upstream Apache-2.0 license is retained in `components/board/LICENSE`.
- `firmware/components/esp_lcd_st7796` retains the upstream panel driver and Espressif Apache-2.0 notices.
- `firmware/components/XPowersLib` contains the subset required for AXP2101. Its MIT license and source notices are retained.
- ESP-IDF v5.5.1 and `espressif/esp_codec_dev` 1.3.4 are external build dependencies; their own licenses apply. Downloaded SDKs, toolchains and managed components are not committed.

Fonts are supplied by the user's operating system. The bridge rasterizes text on that system and sends pixels; no font file is redistributed.
