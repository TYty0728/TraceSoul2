#include "device_control.h"
#include "board.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include <cstdio>
#include <cstring>

static portMUX_TYPE lock=portMUX_INITIALIZER_UNLOCKED;
static char owner[33]={},boot[33]={};
static int64_t deadline=0;
static uint32_t sequence=0,acknowledged=0,revision=0;
static unsigned action=0,phase=0;
static bool active=false,failed=false;
void device_control_init(){
    uint8_t random[16];esp_fill_random(random,sizeof(random));
    for(unsigned i=0;i<16;i++)snprintf(boot+i*2,3,"%02x",random[i]);
}
DeviceControlState device_control_state(){
    portENTER_CRITICAL(&lock);
    bool online=deadline>esp_timer_get_time();
    DeviceControlState result={online,active,sequence>acknowledged&&action==1,sequence>acknowledged&&action==2,failed,phase,revision};
    portEXIT_CRITICAL(&lock);return result;
}
bool device_control_update(const char *controller,uint32_t ack,bool running,bool allowed,unsigned current_phase,bool fault){
    bool pause=false,resume=false;
    portENTER_CRITICAL(&lock);
    int64_t now=esp_timer_get_time();
    bool changed=strcmp(owner,controller)!=0;
    if(changed&&deadline>now){portEXIT_CRITICAL(&lock);return false;}
    if(changed){
        memcpy(owner,controller,33);sequence=acknowledged=action=0;active=false;ack=0;running=false;
    }
    if(ack>sequence){portEXIT_CRITICAL(&lock);return false;}
    deadline=allowed?now+45000000:0;
    if(ack>=sequence||!allowed){
        pause=(active&&!running)||(!running&&action==1&&sequence>acknowledged)||!allowed;
        acknowledged=sequence;active=running&&allowed;phase=current_phase;failed=fault;
        resume=active&&!board_mic_paused();
    }
    revision++;
    portEXIT_CRITICAL(&lock);
    if(pause){board_mic_pause(true);board_stop_playback();}
    else if(resume)board_allow_playback();
    return true;
}
bool device_control_start(){
    portENTER_CRITICAL(&lock);
    if(deadline<=esp_timer_get_time()||active||sequence>acknowledged||sequence==UINT32_MAX){portEXIT_CRITICAL(&lock);return false;}
    board_mic_pause(false); // Publish permission before publishing the new start event.
    sequence++;action=1;failed=false;revision++;
    portEXIT_CRITICAL(&lock);return true;
}
void device_control_stop(){
    // Stop at the driver before waiting for any computer request/acknowledgement.
    board_mic_pause(true);board_stop_playback();
    portENTER_CRITICAL(&lock);
    active=false;phase=5;
    if(sequence<UINT32_MAX){sequence++;action=2;}
    revision++;portEXIT_CRITICAL(&lock);
}
void device_control_tick(){
    bool stop=false;
    portENTER_CRITICAL(&lock);
    if(deadline&&deadline<=esp_timer_get_time()){
        deadline=0;stop=active||sequence>acknowledged;active=false;acknowledged=sequence;phase=5;revision++;
    }
    portEXIT_CRITICAL(&lock);
    if(stop){board_mic_pause(true);board_stop_playback();}
}
void device_control_receipt(char *out,size_t size,uint32_t id){
    char controller[33],identity[33];uint32_t event;unsigned current_action;
    portENTER_CRITICAL(&lock);
    memcpy(controller,owner,sizeof(controller));memcpy(identity,boot,sizeof(identity));event=sequence;
    current_action=event>acknowledged?action:0;
    portEXIT_CRITICAL(&lock);
    snprintf(out,size,"{\"protocol\":1,\"firmware\":\"0.4.6\",\"id\":%lu,\"status\":\"ready\",\"controller\":\"%s\",\"boot\":\"%s\",\"sequence\":%lu,\"action\":\"%s\",\"local_mic_paused\":%s}\n",
        (unsigned long)id,controller,identity,(unsigned long)event,current_action==1?"start":current_action==2?"stop":"",board_mic_paused()?"true":"false");
}
