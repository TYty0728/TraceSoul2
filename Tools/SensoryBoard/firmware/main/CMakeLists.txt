idf_component_register(SRCS "main.cpp" "network.cpp" "native_ui.cpp" "device_control.cpp" INCLUDE_DIRS "."
    REQUIRES board json esp_driver_usb_serial_jtag esp_timer esp-tls esp_wifi esp_event esp_netif nvs_flash lwip)
