#include "board.h"
#include "native_ui.h"
#include "native_labels.h"
#include "terminal.h"
#include "device_control.h"
#include "cJSON.h"
#include "esp_heap_caps.h"
#include "esp_timer.h"
#include "nvs.h"
#include "freertos/task.h"
#include <atomic>
#include <cassert>
#include <cstdio>
#include <cstring>

static constexpr size_t bytes=320*480*2;
static constexpr uint16_t background=0xf7be, ink=0x2967, muted=0x6b8c, card=0xffff, green=0x44b3;
static uint8_t *canvas, *last_frame;
static SemaphoreHandle_t output_mutex;
static std::atomic<bool> menu{false}, has_frame{false};
static std::atomic<unsigned> brightness{60};
static std::atomic<int64_t> last_command{0};
void native_ui_command(){last_command.store(esp_timer_get_time());}
static void save_preferences(){
    nvs_handle_t handle;
    if(nvs_open("terminal_ui",NVS_READWRITE,&handle)!=ESP_OK)return;
    esp_err_t error=nvs_set_u8(handle,"mic_paused",board_mic_paused()?1:0);
    if(error==ESP_OK)error=nvs_set_u8(handle,"brightness",brightness.load());
    if(error==ESP_OK)nvs_commit(handle);
    nvs_close(handle);
}
void native_ui_status(char *out,size_t size){
    snprintf(out,size,"\"native_menu_open\":%s,\"native_has_frame\":%s,\"brightness\":%u",
        menu.load()?"true":"false",has_frame.load()?"true":"false",brightness.load());
}
static void pixel(int x,int y,uint16_t color){
    if(x<0||x>=320||y<0||y>=480)return;
    size_t offset=(y*320+x)*2;canvas[offset]=color>>8;canvas[offset+1]=color&255;
}
static void rectangle(int x,int y,int width,int height,uint16_t color){
    for(int row=y;row<y+height;row++)for(int col=x;col<x+width;col++)pixel(col,row,color);
}
static void label(const NativeLabel &text,int x,int y,uint16_t color=ink){
    for(unsigned row=0;row<text.height;row++)for(unsigned col=0;col<text.width;col++){
        unsigned bit=row*text.width+col;
        if(text.pixels[bit/8]&(0x80>>(bit%8)))pixel(x+col,y+row,color);
    }
}
static void center(const NativeLabel &text,int y,uint16_t color=ink){label(text,(320-text.width)/2,y,color);}
static void settings_icon(){
    rectangle(268,8,44,40,card);
    for(int y=19;y<=35;y+=8)rectangle(278,y,24,2,ink);
}
static void call_button(){
    auto state=device_control_state();
    rectangle(174,8,88,40,card);
    label(state.active||state.starting?label_end_call:label_start_call,180,20,state.online?ink:muted);
}
static const NativeLabel &call_label(){
    auto state=device_control_state();
    if(state.stopping)return label_stopping;
    if(state.starting)return label_starting;
    if(state.failed)return label_call_error;
    if(board_mic_paused())return label_paused;
    if(!state.online)return label_authorise;
    if(!state.active)return label_waiting;
    return state.phase==2?label_listening:state.phase==3?label_thinking:state.phase==4?label_speaking:label_call_ready;
}
static void remote_overlay(){
    settings_icon();call_button();
    auto state=device_control_state();
    if(board_mic_paused()||state.active||state.starting||state.stopping||state.failed){
        rectangle(16,244,288,64,background);center(call_label(),262);
    }
}
// Only Wi-Fi readiness is known here; it never implies a live Soul session.
static int network_state(){
    char state[360];network_status(state,sizeof(state));
    char json[364];snprintf(json,sizeof(json),"{%s}",state);
    cJSON *root=cJSON_Parse(json);
    int result=0;
    if(root){
        if(cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(root,"wifi_connected")))result=2;
        else if(cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(root,"wifi_configured")))result=1;
        cJSON *error=cJSON_GetObjectItemCaseSensitive(root,"wifi_error");
        if(cJSON_IsNumber(error)&&error->valueint)result=3;
    }
    cJSON_Delete(root);return result;
}
static const NativeLabel &network_label(int state){
    return state==2?label_online:state==1?label_connecting:state==3?label_network_error:label_unconfigured;
}
static void render(int network,bool recent){
    if(!menu&&has_frame){memcpy(canvas,last_frame,bytes);remote_overlay();}
    else{
        rectangle(0,0,320,480,background);
        if(menu){
            label(label_settings,20,18);settings_icon();call_button();
            rectangle(16,70,288,128,card);
            label(network_label(network),28,82,network==2?green:ink);
            label(recent?label_recent:label_no_control,28,118,muted);
            label(label_soul,28,154,muted);
            rectangle(16,216,288,52,card);
            label(board_mic_paused()?label_allow:label_privacy,28,231);
            center(board_mic_paused()?label_paused:label_not_listening,281,muted);
            rectangle(16,322,288,52,card);label(label_brightness,28,337);
            label(brightness==30?label_level30:brightness==60?label_level60:label_level90,231,337,green);
            rectangle(16,400,288,52,card);center(label_back,415);
        }else{
            label(label_title,16,20);settings_icon();call_button();
            rectangle(52,100,216,7,green);
            center(label_home,153);center(network_label(network),211,network==2?green:muted);
            center(call_label(),253,muted);
            center(network==0?label_setup_hint:device_control_state().online?label_call_hint:label_authorise,340,muted);
            center(label_home_hint,418,muted);
        }
    }
    board_frame(canvas);
}
// Called with the same output lock as the protocol, so a menu cannot tear a frame.
esp_err_t native_ui_frame(const uint8_t *pixels){
    if(!canvas||!last_frame)return board_frame(pixels);
    memcpy(last_frame,pixels,bytes);has_frame=true;
    if(menu)return ESP_OK; // Keep receiving the latest subtitle behind the settings page.
    memcpy(canvas,pixels,bytes);remote_overlay();return board_frame(canvas);
}
static void run(void *){
    uint32_t seen=board_touch_snapshot().count;
    int previous_network=-1;bool previous_recent=false,dirty=true;
    unsigned previous_call=UINT32_MAX;
    while(true){
        device_control_tick();
        BoardTouch touch=board_touch_snapshot();
        if(touch.testing){seen=touch.count;vTaskDelay(pdMS_TO_TICKS(100));continue;}
        if(touch.count!=seen){
            seen=touch.count;
            if(touch.ready&&touch.x<320&&touch.y<480){
                if(touch.x>=174&&touch.x<264&&touch.y<56){
                    auto state=device_control_state();
                    if(state.active||state.starting){device_control_stop();save_preferences();}
                    else if(device_control_start())save_preferences();
                    dirty=true;
                }
                else if(touch.x>=268&&touch.y<56){menu=!menu;dirty=true;}
                else if(menu&&touch.x>=16&&touch.x<304){
                    if(touch.y>=216&&touch.y<268){board_mic_pause(!board_mic_paused());if(!board_mic_paused())board_allow_playback();save_preferences();dirty=true;}
                    else if(touch.y>=322&&touch.y<374){brightness=brightness==30?60:brightness==60?90:30;board_brightness(brightness);save_preferences();dirty=true;}
                    else if(touch.y>=400&&touch.y<452){menu=false;dirty=true;}
                }
            }
        }
        auto control=device_control_state();
        unsigned call=(unsigned)control.online|((unsigned)control.active<<1)|((unsigned)control.starting<<2)|
            ((unsigned)control.stopping<<3)|((unsigned)control.failed<<4)|(control.phase<<5);
        if(call!=previous_call){dirty=true;previous_call=call;}
        int net=network_state();int64_t command=last_command.load();
        bool recent=command>0&&esp_timer_get_time()-command<30000000;
        if((menu||!has_frame)&&(net!=previous_network||recent!=previous_recent))dirty=true;
        if(xSemaphoreTake(output_mutex,0)==pdTRUE){
            if(dirty){render(net,recent);dirty=false;previous_network=net;previous_recent=recent;}
            xSemaphoreGive(output_mutex);
        }
        vTaskDelay(pdMS_TO_TICKS(100));
    }
}
void native_ui_init(SemaphoreHandle_t lock){
    output_mutex=lock;
    bool paused=false;
    nvs_handle_t handle;
    if(nvs_open("terminal_ui",NVS_READONLY,&handle)==ESP_OK){
        uint8_t value;
        if(nvs_get_u8(handle,"mic_paused",&value)==ESP_OK)paused=value!=0;
        if(nvs_get_u8(handle,"brightness",&value)==ESP_OK&&(value==30||value==60||value==90))brightness=value;
        nvs_close(handle);
    }
    board_mic_pause(paused);
    board_brightness(brightness);
    canvas=(uint8_t *)heap_caps_malloc(bytes,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    last_frame=(uint8_t *)heap_caps_malloc(bytes,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    assert(canvas&&last_frame);
    render(network_state(),false);
    BaseType_t result=xTaskCreate(run,"terminal-ui",6144,nullptr,2,nullptr);assert(result==pdPASS);
}
