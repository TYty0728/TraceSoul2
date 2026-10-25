#include "board.h"
#include "native_ui.h"
#include "native_labels.h"
#include "native_motion.h"
#include "native_scene.h"
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
static SemaphoreHandle_t ui_mutex;
struct UiGuard { UiGuard(){xSemaphoreTakeRecursive(ui_mutex,portMAX_DELAY);} ~UiGuard(){xSemaphoreGiveRecursive(ui_mutex);} };
static uint8_t *scene_data=nullptr;
static NativeScene scene;
static std::atomic<unsigned> scene_page{0},scene_pages{0},phase{0},countdown_value{0},focus_value{0};
static int64_t focus_until=0,countdown_until=0,page_at=0;
static bool human_speech=false;
static unsigned focus_seconds(){int64_t left=focus_until-esp_timer_get_time();return left>0?(unsigned)((left+999999)/1000000):0;}

static std::atomic<bool> menu{false}, has_frame{false};
static std::atomic<bool> artwork_frame{false},capturing{false},hop_active{false};
static std::atomic<unsigned> capture_notice{0},hop_frames{0};
static std::atomic<unsigned> countdown_seen{0};
static std::atomic<unsigned> brightness{60};
static std::atomic<unsigned> volume{60};
static constexpr int menu_mic_top=206,menu_row=52,menu_brightness_top=290,menu_volume_top=350,menu_back_top=410;
static bool menu_row_hit(int y,int top){return y>=top&&y<top+menu_row;}
static std::atomic<int64_t> last_command{0};
void native_ui_command(){last_command.store(esp_timer_get_time());}
static void save_preferences(){
    nvs_handle_t handle;
    if(nvs_open("terminal_ui",NVS_READWRITE,&handle)!=ESP_OK)return;
    esp_err_t error=nvs_set_u8(handle,"mic_paused",board_mic_paused()?1:0);
    if(error==ESP_OK)error=nvs_set_u8(handle,"brightness",brightness.load());
    if(error==ESP_OK)error=nvs_set_u8(handle,"volume",volume.load());
    if(error==ESP_OK)nvs_commit(handle);
    nvs_close(handle);
}
void native_ui_status(char *out,size_t size){
    snprintf(out,size,"\"native_menu_open\":%s,\"native_has_frame\":%s,\"brightness\":%u,\"volume\":%u,\"native_hop_frames\":%u,\"native_hop_active\":%s,\"native_notice\":%u,\"ui_version\":1,\"ui_phase\":%u,\"ui_focus\":%u,\"ui_countdown\":%u,\"ui_page\":%u,\"ui_pages\":%u,\"ui_countdown_seen\":%u",
        menu.load()?"true":"false",has_frame.load()?"true":"false",brightness.load(),volume.load(),hop_frames.load(),hop_active.load()?"true":"false",capture_notice.load(),phase.load(),focus_value.load(),countdown_value.load(),scene_page.load()+1,scene_pages.load(),countdown_seen.load());
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
static void button(int x,int y,int w,int h){
    constexpr int r=8;
    for(int row=0;row<h;row++)for(int col=0;col<w;col++){
        int dx=col<r?r-col:col>=w-r?col-(w-r-1):0;
        int dy=row<r?r-row:row>=h-r?row-(h-r-1):0;
        if(dx*dx+dy*dy<=r*r)pixel(x+col,y+row,card);
    }
}
static void settings_icon(){
    button(268,8,44,40);
    for(int y=19;y<=35;y+=8)rectangle(278,y,24,2,ink);
}
static void call_button(){
    auto state=device_control_state();
    button(174,8,88,40);
    label(!state.online?label_web_control:state.active||state.starting?label_end_call:label_start_call,180,20,state.online?ink:muted);
}
static const NativeLabel &call_label(){
    auto state=device_control_state();
    if(state.stopping)return label_stopping;
    if(state.starting)return label_starting;
    if(state.failed)return label_call_error;
    if(board_mic_paused())return label_paused;
    if(!state.online)return label_authorise;
    if(!state.active)return label_waiting;
    if(capturing)return capture_notice==1?label_owner_unconfirmed:capture_notice==2?label_no_speech:label_listening;
    return state.phase==2?label_listening:state.phase==3?label_thinking:state.phase==4?label_speaking:label_call_ready;
}
static void remote_overlay(){
    settings_icon();call_button();
    rectangle(16,244,288,66,background);
    if(board_mic_paused()){center(label_paused,258);return;}
    if(countdown_value){
        rectangle(0,64,320,180,background);
        center(label_prepare,98);center(*big_labels[countdown_value.load()],145);
        countdown_seen.fetch_or(1u<<countdown_value.load());
        center(label_count_hint,261,muted);return;
    }
    unsigned current=phase;
    unsigned focus=focus_seconds();
    if(current==1&&!focus&&focus_until>0)current=0;
    const NativeLabel &title=current==3?label_thinking:current==4?label_speaking:
        current==5?label_rest:current==6?label_offline:current==7?label_error:
        human_speech||current==2?label_listening:current==1?label_focused:label_standby;
    center(title,249);
    if(capturing&&capture_notice)center(capture_notice==1?label_owner_unconfirmed:label_try_speaking,278,muted);
    else if(current==1&&focus){
        label(label_attention,90,278,muted);label(*digit_labels[focus>30?30:focus],158,278,green);label(label_seconds,193,278,muted);
        rectangle(56,306,208,3,0xe73b);rectangle(56,306,208*focus/30,3,green);
    }else if(capturing)center(label_speak_now,278,muted);
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
    if(!menu&&(has_frame||countdown_value||capturing)){
        if(has_frame)memcpy(canvas,last_frame,bytes);else{rectangle(0,0,320,480,background);label(label_title,16,20);}
        remote_overlay();
    }
    else{
        rectangle(0,0,320,480,background);
        if(menu){
            label(label_settings,20,18);settings_icon();call_button();
            rectangle(16,70,288,128,card);
            label(network_label(network),28,82,network==2?green:ink);
            label(recent?label_recent:label_no_control,28,118,muted);
            label(label_soul,28,154,muted);
            rectangle(16,menu_mic_top,288,menu_row,card);
            label(board_mic_paused()?label_allow:label_privacy,28,menu_mic_top+15);
            center(board_mic_paused()?label_paused:label_not_listening,menu_mic_top+menu_row+4,muted);
            rectangle(16,menu_brightness_top,288,menu_row,card);label(label_brightness,28,menu_brightness_top+15);
            label(brightness==30?label_level30:brightness==60?label_level60:label_level90,231,menu_brightness_top+15,green);
            rectangle(16,menu_volume_top,288,menu_row,card);label(label_volume,28,menu_volume_top+15);
            const NativeLabel &level=volume==30?label_vol30:volume==100?label_vol100:label_vol60;
            label(level,271-(int)level.width,menu_volume_top+15,green);
            rectangle(16,menu_back_top,288,menu_row,card);center(label_back,menu_back_top+15);
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
// The UI lock protects cached pixels; microphone/audio hold only the protocol lock.
esp_err_t native_ui_frame(const uint8_t *pixels,bool artwork){
    UiGuard guard;
    if(!canvas||!last_frame)return board_frame(pixels);
    heap_caps_free(scene_data);scene_data=nullptr;scene_pages=0;
    memcpy(last_frame,pixels,bytes);has_frame=true;artwork_frame=artwork;hop_active=false;
    if(menu)return ESP_OK; // Keep receiving the latest subtitle behind the settings page.
    memcpy(canvas,pixels,bytes);remote_overlay();return board_frame(canvas);
}
// Notices are local pixels, serialized with animation by the independent UI lock.
void native_ui_capture(bool recording,unsigned notice){
    UiGuard guard;
    if(!recording&&human_speech)phase=3;
    capturing=recording;human_speech=false;capture_notice=recording?notice:0;hop_active=false;
    if(!menu)render(network_state(),true);
}
void native_ui_speech(){
    UiGuard guard;if(human_speech)return;human_speech=true;capture_notice=0;
    if(!menu&&has_frame){memcpy(canvas,last_frame,bytes);remote_overlay();board_frame(canvas);}
}
esp_err_t native_ui_scene(const uint8_t *data,size_t size,bool artwork){
    NativeScene parsed;
    if(!scene_parse(data,size,parsed))return ESP_ERR_INVALID_ARG;
    auto *copy=(uint8_t*)heap_caps_malloc(size,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    if(!copy)return ESP_ERR_NO_MEM;
    memcpy(copy,data,size);
    UiGuard guard;
    heap_caps_free(scene_data);scene_data=copy;scene=parsed;
    scene_page=0;scene_pages=scene.count;phase=scene.phase;if(phase==1)focus_until=0;human_speech=false;
    page_at=esp_timer_get_time()+int64_t(scene.interval)*1000;
    scene_region(copy+scene.offset[0],scene.length[0],scene.codec[0],320*480,last_frame);
    has_frame=true;artwork_frame=artwork;
    if(menu)return ESP_OK;
    memcpy(canvas,last_frame,bytes);remote_overlay();return board_frame(canvas);
}
static void show_page(unsigned page){
    if(!scene_data||page>=scene.count)return;
    size_t offset=page?317*320*2:0;
    scene_region(scene_data+scene.offset[page],scene.length[page],scene.codec[page],320*(page?163:480),last_frame+offset);
    scene_page=page;page_at=esp_timer_get_time()+int64_t(scene.interval)*1000;
    memcpy(canvas+317*320*2,last_frame+317*320*2,320*163*2);
    board_frame_region(canvas,317,163);
}
void native_ui_phase(unsigned value,unsigned focus_ms){
    UiGuard guard;
    if(value==4&&phase!=4&&scene_pages>1&&!menu)show_page(0);
    phase=value;focus_until=focus_ms?esp_timer_get_time()+int64_t(focus_ms)*1000:0;
    focus_value=focus_seconds();human_speech=false;
    if(!menu&&has_frame){memcpy(canvas,last_frame,bytes);remote_overlay();board_frame(canvas);}
}
void native_ui_countdown(unsigned seconds){
    UiGuard guard;countdown_until=seconds?esp_timer_get_time()+int64_t(seconds)*1000000:0;
    countdown_value=seconds;if(seconds)countdown_seen=0;
    if(!menu)render(network_state(),true);
}
static esp_err_t render_hop(NativePose pose){
    // Only the artwork band moves. Header, controls, status and subtitle stay fixed.
    constexpr int top=64,bottom=244;
    for(int y=top;y<bottom;y++)for(int x=0;x<320;x++){
        int source_x=160+(x-160)*1000/pose.sx;
        int source_y=240+(y-240-pose.lift)*1000/pose.sy;
        size_t source=(source_x>=0&&source_x<320&&source_y>=top&&source_y<bottom)
            ?(source_y*320+source_x)*2:top*320*2;
        size_t target=(y*320+x)*2;canvas[target]=last_frame[source];canvas[target+1]=last_frame[source+1];
    }
    auto result=board_frame_region(canvas,top,bottom-top);
    if(result==ESP_OK)hop_frames++;
    return result;
}
static void run(void *){
    uint32_t seen=board_touch_snapshot().count;
    int previous_network=-1;bool previous_recent=false,dirty=true;
    unsigned previous_call=UINT32_MAX;
    bool idle_before=false;int64_t idle_since=0;NativePose previous_pose;
    unsigned previous_focus=999,previous_countdown=999;
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
                else if(!menu&&touch.y>=317&&scene_pages>1){UiGuard guard;show_page((scene_page+(touch.x<160?scene_pages-1:1))%scene_pages);page_at=esp_timer_get_time()+15000000;}
                else if(menu&&touch.x>=16&&touch.x<304){
                    if(menu_row_hit(touch.y,menu_mic_top)){board_mic_pause(!board_mic_paused());if(!board_mic_paused())board_allow_playback();save_preferences();dirty=true;}
                    else if(menu_row_hit(touch.y,menu_brightness_top)){brightness=brightness==30?60:brightness==60?90:30;board_brightness(brightness);save_preferences();dirty=true;}
                    else if(menu_row_hit(touch.y,menu_volume_top)){
                        unsigned next=volume==60?100:volume==100?30:60;
                        if(board_volume(next)==ESP_OK){volume=next;save_preferences();dirty=true;}
                    }
                    else if(menu_row_hit(touch.y,menu_back_top)){menu=false;dirty=true;}
                }
            }
        }
        { UiGuard guard;
        auto control=device_control_state();
        int64_t clock_now=esp_timer_get_time();
        focus_value=focus_seconds();
        countdown_value=countdown_until>clock_now?(unsigned)((countdown_until-clock_now+999999)/1000000):0;
        if(previous_focus!=focus_value||previous_countdown!=countdown_value){dirty=true;previous_focus=focus_value;previous_countdown=countdown_value;}
        if(!menu&&scene_pages>1&&clock_now>=page_at)show_page((scene_page+1)%scene_pages);
        bool idle=!menu&&has_frame&&artwork_frame&&!human_speech&&!countdown_value&&phase<=2&&
            !control.starting&&!control.stopping&&!control.failed;
        int64_t now=esp_timer_get_time();if(idle&&!idle_before)idle_since=now;
        NativePose pose=idle?native_hop_pose((now-idle_since)/1000):NativePose{};
        hop_active=idle&&!native_pose_equal(pose,{});idle_before=idle;
        unsigned call=(unsigned)control.online|((unsigned)control.active<<1)|((unsigned)control.starting<<2)|
            ((unsigned)control.stopping<<3)|((unsigned)control.failed<<4)|(control.phase<<5);
        if(call!=previous_call){dirty=true;previous_call=call;}
        int net=network_state();int64_t command=last_command.load();
        bool recent=command>0&&esp_timer_get_time()-command<30000000;
        if((menu||!has_frame)&&(net!=previous_network||recent!=previous_recent))dirty=true;
        {
            if(dirty){render(net,recent);dirty=false;previous_network=net;previous_recent=recent;previous_pose={};}
            if(!menu&&has_frame&&!countdown_value&&!native_pose_equal(pose,previous_pose)){
                if(render_hop(pose)==ESP_OK)previous_pose=pose;
            }

        }
        }
        vTaskDelay(pdMS_TO_TICKS(40));
    }
}
void native_ui_init(SemaphoreHandle_t lock){
    (void)lock;ui_mutex=xSemaphoreCreateRecursiveMutex();assert(ui_mutex);
    bool paused=false;
    nvs_handle_t handle;
    if(nvs_open("terminal_ui",NVS_READONLY,&handle)==ESP_OK){
        uint8_t value;
        if(nvs_get_u8(handle,"mic_paused",&value)==ESP_OK)paused=value!=0;
        if(nvs_get_u8(handle,"brightness",&value)==ESP_OK&&(value==30||value==60||value==90))brightness=value;
        if(nvs_get_u8(handle,"volume",&value)==ESP_OK&&(value==30||value==60||value==100))volume=value;
        nvs_close(handle);
    }
    board_mic_pause(paused);
    board_brightness(brightness);
    if(board_volume(volume)!=ESP_OK)volume=60;
    canvas=(uint8_t *)heap_caps_malloc(bytes,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    last_frame=(uint8_t *)heap_caps_malloc(bytes,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    assert(canvas&&last_frame);
    render(network_state(),false);
    BaseType_t result=xTaskCreate(run,"terminal-ui",6144,nullptr,2,nullptr);assert(result==pdPASS);
}
