#include "terminal.h"
#include <cstring>
#include <cstdio>
#include "cJSON.h"
#include "driver/usb_serial_jtag.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "nvs_flash.h"
#include "nvs.h"
#include "lwip/sockets.h"
#include "lwip/tcp.h"
#include "mbedtls/ssl.h"

static char ssid[33], password[65], token[65], certificate[2048], private_key[2048];
static esp_netif_t *station;
static volatile bool connected = false;
static volatile int last_reason = 0, reconnects = 0;
static esp_err_t startup_error = ESP_OK;
static const char *startup_stage = "unconfigured";

int TerminalLink::read(void *data, size_t size, TickType_t wait) {
    if (!tls) return usb_serial_jtag_read_bytes(data, size, wait);
    int result = esp_tls_conn_read(tls, data, size);
    if (result == MBEDTLS_ERR_SSL_WANT_READ || result == MBEDTLS_ERR_SSL_WANT_WRITE || result == MBEDTLS_ERR_SSL_TIMEOUT) return 0;
    if (result <= 0) closed = true;
    return result;
}
int TerminalLink::write(const void *data, size_t size, TickType_t wait) {
    if (!tls) return usb_serial_jtag_write_bytes(data, size, wait);
    size_t offset = 0;
    int64_t deadline = esp_timer_get_time() + 5000000;
    while (offset < size && esp_timer_get_time() < deadline) {
        int n = esp_tls_conn_write(tls, (const uint8_t *)data + offset, size - offset);
        if (n == MBEDTLS_ERR_SSL_WANT_WRITE || n == MBEDTLS_ERR_SSL_WANT_READ) { vTaskDelay(1); continue; }
        if (n <= 0) { closed = true; break; }
        offset += n;
    }
    if (offset != size) closed = true;
    return offset;
}
static bool field(cJSON *json, const char *name, char *out, size_t capacity, size_t minimum) {
    cJSON *item = cJSON_GetObjectItemCaseSensitive(json, name);
    if (!cJSON_IsString(item)) return false;
    size_t length = strlen(item->valuestring);
    if (length < minimum || length >= capacity) return false;
    memcpy(out, item->valuestring, length + 1);
    return true;
}
static bool validate(cJSON *json) {
    char s[33], p[65], t[65], c[2048], k[2048];
    if (!field(json,"ssid",s,sizeof(s),1) || !field(json,"password",p,sizeof(p),8) ||
        !field(json,"token",t,sizeof(t),64) || !field(json,"certificate",c,sizeof(c),100) ||
        !field(json,"private_key",k,sizeof(k),100)) return false;
    for (char ch : t) if (ch && !((ch>='a'&&ch<='f')||(ch>='0'&&ch<='9'))) return false;
    return strstr(c,"-----BEGIN CERTIFICATE-----") == c && strstr(k,"-----BEGIN PRIVATE KEY-----") == k;
}
bool network_save(const char *data, size_t size) {
    if (size > 7000) return false;
    cJSON *json = cJSON_ParseWithLength(data, size);
    bool valid = json && validate(json);
    cJSON_Delete(json);
    if (!valid) return false;
    nvs_handle_t handle;
    if (nvs_open("tracesoul",NVS_READWRITE,&handle)!=ESP_OK) return false;
    esp_err_t err = nvs_set_blob(handle,"network",data,size);
    if (err==ESP_OK) err=nvs_commit(handle);
    nvs_close(handle);
    return err==ESP_OK;
}
void network_status(char *out, size_t size) {
    esp_netif_ip_info_t ip = {};
    if (station && connected) esp_netif_get_ip_info(station,&ip);
    snprintf(out,size,"\"wifi_configured\":%s,\"wifi_connected\":%s,\"wifi_ip\":\"" IPSTR "\",\"wifi_port\":5443,\"wifi_stage\":\"%s\",\"wifi_error\":%d,\"wifi_reason\":%d,\"wifi_reconnects\":%d",
        token[0]?"true":"false",connected?"true":"false",IP2STR(&ip.ip),startup_stage,(int)startup_error,last_reason,reconnects);
}
static void event(void *, esp_event_base_t base, int32_t id, void *data) {
    if (base==WIFI_EVENT && id==WIFI_EVENT_STA_START) esp_wifi_connect();
    if (base==WIFI_EVENT && id==WIFI_EVENT_STA_DISCONNECTED) {
        connected=false; last_reason=((wifi_event_sta_disconnected_t *)data)->reason;
        if (reconnects<1000000) reconnects++;
        esp_wifi_connect();
    }
    if (base==IP_EVENT && id==IP_EVENT_STA_GOT_IP) connected=true;
}
static bool authenticate(TerminalLink &link) {
    char line[256]; size_t count=0; bool complete=false;
    int64_t deadline=esp_timer_get_time()+3000000;
    while (count+1<sizeof(line) && esp_timer_get_time()<deadline && !link.closed) {
        char ch;
        if (link.read(&ch,1,0)!=1) continue;
        if (ch=='\n') { complete=true; break; }
        line[count++]=ch;
    }
    line[count]=0;
    cJSON *json=complete?cJSON_Parse(line):nullptr;
    cJSON *value=json?cJSON_GetObjectItemCaseSensitive(json,"token"):nullptr;
    bool valid=cJSON_IsString(value) && strlen(value->valuestring)==64;
    unsigned difference=0;
    if (valid) for (size_t i=0;i<64;++i) difference|=(unsigned char)value->valuestring[i]^(unsigned char)token[i];
    valid=valid && difference==0;
    cJSON_Delete(json);
    memset(line,0,sizeof(line));
    if (valid) { const char *ok="{\"authenticated\":true}\n"; link.write(ok,strlen(ok),0); }
    return valid;
}
static void serve(void *) {
    int listener=socket(AF_INET,SOCK_STREAM,IPPROTO_IP);
    if (listener<0) { vTaskDelete(nullptr); return; }
    int reuse=1; setsockopt(listener,SOL_SOCKET,SO_REUSEADDR,&reuse,sizeof(reuse));
    sockaddr_in address={}; address.sin_family=AF_INET; address.sin_port=htons(5443); address.sin_addr.s_addr=htonl(INADDR_ANY);
    if (bind(listener,(sockaddr *)&address,sizeof(address))!=0 || listen(listener,1)!=0) { close(listener); vTaskDelete(nullptr); return; }
    while (true) {
        int client=accept(listener,nullptr,nullptr);
        if (client<0) { vTaskDelay(pdMS_TO_TICKS(100)); continue; }
        int no_delay=1; setsockopt(client,IPPROTO_TCP,TCP_NODELAY,&no_delay,sizeof(no_delay));
        timeval timeout={5,0}; setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));
        setsockopt(client,SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof(timeout));
        esp_tls_t *tls=esp_tls_init();
        if (!tls) { close(client); continue; }
        esp_tls_cfg_server_t config={};
        config.servercert_buf=(const unsigned char *)certificate; config.servercert_bytes=strlen(certificate)+1;
        config.serverkey_buf=(const unsigned char *)private_key; config.serverkey_bytes=strlen(private_key)+1;
        if (esp_tls_server_session_create(&config,client,tls)==0) {
            timeout={0,100000}; setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));
            TerminalLink link; link.tls=tls;
            if (authenticate(link)) terminal_run(link);
        }
        esp_tls_server_session_delete(tls);
        close(client);
    }
}
void network_init() {
    // Never erase an existing NVS partition to recover an initialization error.
    if (nvs_flash_init()!=ESP_OK) return;
    nvs_handle_t handle;
    if (nvs_open("tracesoul",NVS_READONLY,&handle)!=ESP_OK) return;
    char data[7001]={}; size_t size=7000;
    esp_err_t err=nvs_get_blob(handle,"network",data,&size); nvs_close(handle);
    if (err!=ESP_OK) return;
    cJSON *json=cJSON_ParseWithLength(data,size);
    if (!json || !validate(json)) { cJSON_Delete(json); return; }
    field(json,"ssid",ssid,sizeof(ssid),1); field(json,"password",password,sizeof(password),8);
    field(json,"token",token,sizeof(token),64); field(json,"certificate",certificate,sizeof(certificate),100);
    field(json,"private_key",private_key,sizeof(private_key),100); cJSON_Delete(json); memset(data,0,sizeof(data));
    startup_stage="netif"; startup_error=esp_netif_init(); if (startup_error!=ESP_OK) return;
    startup_stage="event_loop"; startup_error=esp_event_loop_create_default(); if (startup_error!=ESP_OK) return;
    station=esp_netif_create_default_wifi_sta();
    wifi_init_config_t init=WIFI_INIT_CONFIG_DEFAULT();
    startup_stage="wifi_init"; startup_error=station?esp_wifi_init(&init):ESP_ERR_NO_MEM; if(startup_error!=ESP_OK)return;
    esp_wifi_set_storage(WIFI_STORAGE_RAM);
    esp_event_handler_register(WIFI_EVENT,ESP_EVENT_ANY_ID,event,nullptr);
    esp_event_handler_register(IP_EVENT,IP_EVENT_STA_GOT_IP,event,nullptr);
    wifi_config_t config={}; memcpy(config.sta.ssid,ssid,strlen(ssid)); memcpy(config.sta.password,password,strlen(password));
    config.sta.threshold.authmode=WIFI_AUTH_WPA2_PSK; config.sta.pmf_cfg.capable=true;
    startup_stage="mode"; startup_error=esp_wifi_set_mode(WIFI_MODE_STA); if(startup_error!=ESP_OK)return;
    startup_stage="configuration"; startup_error=esp_wifi_set_config(WIFI_IF_STA,&config); if(startup_error!=ESP_OK)return;
    startup_stage="start"; startup_error=esp_wifi_start(); if(startup_error!=ESP_OK)return;
    startup_stage="running";
    esp_wifi_set_ps(WIFI_PS_NONE);
    xTaskCreate(serve,"wifi-terminal",16384,nullptr,4,nullptr);
}
