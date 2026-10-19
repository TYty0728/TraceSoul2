idf_component_register(SRCS "board.cpp" "esp_axp2101_port.cpp" "esp_es8311_port.cpp"
    INCLUDE_DIRS "." REQUIRES driver esp_lcd esp_lcd_st7796 XPowersLib esp_codec_dev)
