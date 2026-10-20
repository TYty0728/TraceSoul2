// Wiring and LCD initialization adapted from Waveshare ESP32-S3-Touch-LCD-3.5.
// See THIRD_PARTY.md and the accompanying upstream driver licenses.
#include "board.h"
#include <cstring>
#include <cstdio>
#include <atomic>
#include "driver/i2c_master.h"
#include "driver/spi_master.h"
#include "driver/ledc.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_st7796.h"
#include "esp_heap_caps.h"
#include "esp_codec_dev.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "esp_axp2101_port.h"

#if !CONFIG_XPOWERS_ESP_IDF_NEW_API
#error "AXP2101 must use the ESP-IDF new I2C API; mixed drivers abort before app_main"
#endif
#if CONFIG_CODEC_I2C_BACKWARD_COMPATIBLE
#error "ES8311 must use the same new I2C API as the board"
#endif

void esp_es8311_port_init(i2c_master_bus_handle_t bus);
extern esp_codec_dev_handle_t output_dev;
extern esp_codec_dev_handle_t input_dev;
static esp_lcd_panel_handle_t panel;
static SemaphoreHandle_t transferred;
static uint8_t *stripe;
static i2c_master_dev_handle_t expander;
static i2c_master_dev_handle_t touch;
static SemaphoreHandle_t lcd_lock;
static portMUX_TYPE touch_lock=portMUX_INITIALIZER_UNLOCKED;
static bool touch_ready=false, touch_down=false, touch_testing=false;
static uint16_t touch_x=0, touch_y=0;
static uint32_t touch_count=0;
static std::atomic<bool> mic_paused{false};
static std::atomic<unsigned> mic_epoch{0};
static unsigned recording_epoch=0;
static std::atomic<unsigned> playback_epoch{0};
static std::atomic<bool> playback_blocked{false};
void board_stop_playback(){playback_blocked.store(true);playback_epoch.fetch_add(1);}
void board_allow_playback(){playback_blocked.store(false);}
void board_mic_pause(bool paused){mic_paused.store(paused);if(paused)mic_epoch.fetch_add(1);}
bool board_mic_paused(){return mic_paused.load();}
void board_brightness(unsigned percent){
    ledc_set_duty(LEDC_LOW_SPEED_MODE,LEDC_CHANNEL_0,(percent>100?100:percent)*1023/100);
    ledc_update_duty(LEDC_LOW_SPEED_MODE,LEDC_CHANNEL_0);
}
BoardTouch board_touch_snapshot(){
    portENTER_CRITICAL(&touch_lock);
    BoardTouch result={touch_ready,touch_down,touch_testing,touch_x,touch_y,touch_count};
    portEXIT_CRITICAL(&touch_lock);
    return result;
}
static void poll_touch(void *) {
    bool previously_down=false;
    while(true) {
        uint8_t reg=0x02, data[5]={};
        esp_err_t err=i2c_master_transmit_receive(touch,&reg,1,data,sizeof(data),100);
        bool down=err==ESP_OK && (data[0]&15)>0 && (data[0]&15)<=2;
        uint16_t x=((data[1]&15)<<8)|data[2], y=((data[3]&15)<<8)|data[4];
        bool marker=false;
        portENTER_CRITICAL(&touch_lock);
        touch_ready=err==ESP_OK; touch_down=down;
        if(down){touch_x=x;touch_y=y;if(!previously_down)touch_count++;}
        marker=down && touch_testing && x<320 && y<480;
        portEXIT_CRITICAL(&touch_lock);
        if(marker && xSemaphoreTake(lcd_lock,pdMS_TO_TICKS(50))==pdTRUE){
            int left=x>4?x-4:0,top=y>4?y-4:0,right=x<315?x+5:320,bottom=y<475?y+5:480;
            for(int i=0;i<(right-left)*(bottom-top);i++){stripe[2*i]=0x07;stripe[2*i+1]=0xe0;}
            if(esp_lcd_panel_draw_bitmap(panel,left,top,right,bottom,stripe)==ESP_OK)xSemaphoreTake(transferred,pdMS_TO_TICKS(500));
            xSemaphoreGive(lcd_lock);
        }
        previously_down=down;
        vTaskDelay(pdMS_TO_TICKS(40));
    }
}
void board_touch_status(char *out,size_t size){
    portENTER_CRITICAL(&touch_lock);
    bool ready=touch_ready,down=touch_down,testing=touch_testing;
    uint16_t x=touch_x,y=touch_y;uint32_t count=touch_count;
    portEXIT_CRITICAL(&touch_lock);
    snprintf(out,size,"\"touch_ready\":%s,\"touch_down\":%s,\"touch_test\":%s,\"touch_x\":%u,\"touch_y\":%u,\"touch_count\":%lu",
        ready?"true":"false",down?"true":"false",testing?"true":"false",x,y,(unsigned long)count);
}
void board_touch_test(bool enabled){portENTER_CRITICAL(&touch_lock);touch_testing=enabled;portEXIT_CRITICAL(&touch_lock);}
static esp_err_t amplifier(bool enabled) {
    uint8_t reg = 1, value;
    esp_err_t err = i2c_master_transmit_receive(expander, &reg, 1, &value, 1, 1000);
    if (err != ESP_OK) return err;
    // Schematic: TCA9554 EXIO7 -> NS4150B PA_CTRL, active high.
    uint8_t data[] = {1, uint8_t(enabled ? value | 0x80 : value & ~0x80)};
    return i2c_master_transmit(expander, data, sizeof(data), 1000);
}
static bool on_transfer(esp_lcd_panel_io_handle_t, esp_lcd_panel_io_event_data_t *, void *) {
    BaseType_t wake = pdFALSE;
    xSemaphoreGiveFromISR(transferred, &wake);
    return wake == pdTRUE;
}

void board_init() {
    i2c_master_bus_config_t bus_cfg = {};
    bus_cfg.i2c_port = I2C_NUM_0;
    bus_cfg.sda_io_num = GPIO_NUM_8;
    bus_cfg.scl_io_num = GPIO_NUM_7;
    bus_cfg.clk_source = I2C_CLK_SRC_DEFAULT;
    bus_cfg.glitch_ignore_cnt = 7;
    bus_cfg.flags.enable_internal_pullup = true;
    i2c_master_bus_handle_t bus;
    ESP_ERROR_CHECK(i2c_new_master_bus(&bus_cfg, &bus));
    // EXIO1 resets the panel; EXIO7 enables the speaker amplifier.
    i2c_device_config_t exp_cfg = {};
    exp_cfg.device_address = 0x20;
    exp_cfg.dev_addr_length = I2C_ADDR_BIT_LEN_7;
    exp_cfg.scl_speed_hz = 400000;
    ESP_ERROR_CHECK(i2c_master_bus_add_device(bus, &exp_cfg, &expander));
    ESP_ERROR_CHECK(amplifier(false));
    uint8_t reg = 3, value;
    ESP_ERROR_CHECK(i2c_master_transmit_receive(expander, &reg, 1, &value, 1, 1000));
    uint8_t data[] = {3, uint8_t(value & ~0x82)};
    ESP_ERROR_CHECK(i2c_master_transmit(expander, data, 2, 1000));
    reg = 1;
    ESP_ERROR_CHECK(i2c_master_transmit_receive(expander, &reg, 1, &value, 1, 1000));
    data[0] = 1; data[1] = value & ~2;
    ESP_ERROR_CHECK(i2c_master_transmit(expander, data, 2, 1000));
    vTaskDelay(pdMS_TO_TICKS(100));
    data[1] = value | 2;
    ESP_ERROR_CHECK(i2c_master_transmit(expander, data, 2, 1000));
    vTaskDelay(pdMS_TO_TICKS(100));
    ESP_ERROR_CHECK(esp_axp2101_port_init(bus));

    transferred = xSemaphoreCreateBinary();
    stripe = (uint8_t *)heap_caps_malloc(320 * 20 * 2, MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
    assert(transferred && stripe);
    spi_bus_config_t spi = {};
    spi.sclk_io_num = 5; spi.mosi_io_num = 1; spi.miso_io_num = -1;
    spi.quadwp_io_num = -1; spi.quadhd_io_num = -1;
    spi.max_transfer_sz = 320 * 20 * 2;
    ESP_ERROR_CHECK(spi_bus_initialize(SPI2_HOST, &spi, SPI_DMA_CH_AUTO));
    esp_lcd_panel_io_spi_config_t io_cfg = {};
    io_cfg.cs_gpio_num = -1; io_cfg.dc_gpio_num = 3;
    io_cfg.spi_mode = 0; io_cfg.pclk_hz = 40000000;
    io_cfg.trans_queue_depth = 1;
    io_cfg.lcd_cmd_bits = 8; io_cfg.lcd_param_bits = 8;
    io_cfg.on_color_trans_done = on_transfer;
    esp_lcd_panel_io_handle_t io;
    ESP_ERROR_CHECK(esp_lcd_new_panel_io_spi((esp_lcd_spi_bus_handle_t)SPI2_HOST, &io_cfg, &io));
    esp_lcd_panel_dev_config_t lcd = {};
    lcd.reset_gpio_num = -1; lcd.rgb_ele_order = LCD_RGB_ELEMENT_ORDER_BGR;
    lcd.bits_per_pixel = 16;
    ESP_ERROR_CHECK(esp_lcd_new_panel_st7796(io, &lcd, &panel));
    ESP_ERROR_CHECK(esp_lcd_panel_reset(panel));
    ESP_ERROR_CHECK(esp_lcd_panel_init(panel));
    ESP_ERROR_CHECK(esp_lcd_panel_invert_color(panel, true));
    ESP_ERROR_CHECK(esp_lcd_panel_mirror(panel, true, false));
    ESP_ERROR_CHECK(esp_lcd_panel_disp_on_off(panel, true));
    ledc_timer_config_t timer = {};
    timer.speed_mode = LEDC_LOW_SPEED_MODE; timer.timer_num = LEDC_TIMER_1;
    timer.duty_resolution = LEDC_TIMER_10_BIT; timer.freq_hz = 5000;
    timer.clk_cfg = LEDC_AUTO_CLK;
    ESP_ERROR_CHECK(ledc_timer_config(&timer));
    ledc_channel_config_t light = {};
    light.speed_mode = LEDC_LOW_SPEED_MODE; light.channel = LEDC_CHANNEL_0;
    light.timer_sel = LEDC_TIMER_1; light.gpio_num = 6; light.duty = 614;
    ESP_ERROR_CHECK(ledc_channel_config(&light));
    esp_es8311_port_init(bus);
    ESP_ERROR_CHECK(esp_codec_dev_set_out_vol(output_dev, 45));
    lcd_lock=xSemaphoreCreateMutex();assert(lcd_lock);
    i2c_device_config_t touch_cfg={};touch_cfg.device_address=0x38;touch_cfg.dev_addr_length=I2C_ADDR_BIT_LEN_7;touch_cfg.scl_speed_hz=400000;
    if(i2c_master_bus_add_device(bus,&touch_cfg,&touch)==ESP_OK)xTaskCreate(poll_touch,"touch-input",4096,nullptr,3,nullptr);
}

esp_err_t board_frame(const uint8_t *rgb565) {
    if(xSemaphoreTake(lcd_lock,pdMS_TO_TICKS(2000))!=pdTRUE)return ESP_ERR_TIMEOUT;
    for (int y = 0; y < 480; y += 20) {
        memcpy(stripe, rgb565 + y * 320 * 2, 320 * 20 * 2);
        esp_err_t err = esp_lcd_panel_draw_bitmap(panel, 0, y, 320, y + 20, stripe);
        if (err != ESP_OK) {xSemaphoreGive(lcd_lock);return err;}
        if (!xSemaphoreTake(transferred, pdMS_TO_TICKS(2000))) {xSemaphoreGive(lcd_lock);return ESP_ERR_TIMEOUT;}
    }
    xSemaphoreGive(lcd_lock);return ESP_OK;
}

esp_err_t board_audio(const uint8_t *pcm, size_t size) {
    if(playback_blocked.load())return ESP_ERR_INVALID_STATE;
    unsigned epoch=playback_epoch.load();
    esp_err_t err = amplifier(true);
    if (err != ESP_OK) return err;
    vTaskDelay(pdMS_TO_TICKS(20));
    // Micro-slices bound write latency and feed the scheduler; 48kHz mono PCM16.
    for (size_t offset = 0; offset < size; offset += 4096) {
        if(playback_blocked.load()||epoch!=playback_epoch.load()){amplifier(false);return ESP_ERR_INVALID_STATE;}
        size_t n = size - offset < 4096 ? size - offset : 4096;
        if (esp_codec_dev_write(output_dev, (void *)(pcm + offset), n) != ESP_CODEC_DEV_OK) {
            amplifier(false);
            return ESP_FAIL;
        }
        vTaskDelay(1);
    }
    // Let the final DMA samples drain before reporting playback completion.
    vTaskDelay(pdMS_TO_TICKS(150));
    return amplifier(false);
}

esp_err_t board_record_begin(uint8_t *pcm) {
    if(board_mic_paused())return ESP_ERR_INVALID_STATE;
    recording_epoch=mic_epoch.load();
    esp_err_t err = amplifier(false);
    if (err != ESP_OK) return err;
    if (esp_codec_dev_set_in_gain(input_dev, 30.0) != ESP_CODEC_DEV_OK) return ESP_FAIL;
    // Drain a short slice so the RX DMA does not contribute stale samples.
    // Reuse PSRAM, not the small main-task stack, for discarded samples.
    if (esp_codec_dev_read(input_dev, pcm, 4096) != ESP_CODEC_DEV_OK) return ESP_FAIL;
    return ESP_OK;
}

esp_err_t board_record_read(uint8_t *pcm, size_t size) {
    if(board_mic_paused()||recording_epoch!=mic_epoch.load())return ESP_ERR_INVALID_STATE;
    int result=esp_codec_dev_read(input_dev, pcm, size);
    if(board_mic_paused()||recording_epoch!=mic_epoch.load())return ESP_ERR_INVALID_STATE;
    return result == ESP_CODEC_DEV_OK ? ESP_OK : ESP_FAIL;
}

esp_err_t board_record(uint8_t *pcm, size_t size) {
    esp_err_t err = board_record_begin(pcm);
    if (err != ESP_OK) return err;
    for (size_t offset = 0; offset < size; offset += 4096) {
        if(board_mic_paused()||recording_epoch!=mic_epoch.load())return ESP_ERR_INVALID_STATE;
        size_t n = size - offset < 4096 ? size - offset : 4096;
        if (esp_codec_dev_read(input_dev, pcm + offset, n) != ESP_CODEC_DEV_OK) return ESP_FAIL;
        vTaskDelay(1);
    }
    return board_mic_paused()||recording_epoch!=mic_epoch.load()?ESP_ERR_INVALID_STATE:ESP_OK;
}
