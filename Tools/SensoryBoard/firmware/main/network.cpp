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
#include "mbedtls/md.h"
#include "lwip/netdb.h"

static char ssid[33], password[65], token[65], certificate[2048], private_key[2048];
static esp_netif_t *station;
static volatile bool connected = false;
static volatile int last_reason = 0, reconnects = 0;
static esp_err_t startup_error = ESP_OK;
static const char *startup_stage = "unconfigured";
static char relay_host[254], relay_key[65];
static int relay_port = 5444;
static const char *relay_stage = "disabled";


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
static bool relay_validate(cJSON *json, char *host, char *key, int &port) {
    auto *p = cJSON_GetObjectItemCaseSensitive(json,"port");
    if (!field(json,"host",host,254,1) || !field(json,"key",key,65,64) ||
        !cJSON_IsNumber(p) || p->valuedouble != p->valueint || p->valueint < 1024 || p->valueint > 65535) return false;
    if (strspn(key,"0123456789abcdef") != 64 || strspn(host,"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-") != strlen(host)) return false;
    port=p->valueint;
    return true;
}
bool network_relay_save(const char *data, size_t size) {
    if (size>1024) return false;
    char host[254], key[65]; int port=0;
    cJSON *json=cJSON_ParseWithLength(data,size);
    bool valid=json && relay_validate(json,host,key,port);
    cJSON_Delete(json); memset(key,0,sizeof(key));
    if (!valid) return false;
    nvs_handle_t handle;
    if (nvs_open("tracesoul",NVS_READWRITE,&handle)!=ESP_OK) return false;
    esp_err_t err=nvs_set_blob(handle,"relay",data,size);
    if (err==ESP_OK) err=nvs_commit(handle);
    nvs_close(handle);
    return err==ESP_OK;
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
    size_t used=strlen(out);
    if (used<size) snprintf(out+used,size-used,",\"relay_stage\":\"%s\"",relay_stage);
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
static void serve_client(int client) {
    int no_delay=1; setsockopt(client,IPPROTO_TCP,TCP_NODELAY,&no_delay,sizeof(no_delay));
    timeval timeout={8,0}; setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));
    setsockopt(client,SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof(timeout));
    esp_tls_t *tls=esp_tls_init();
    if (!tls) return;
    esp_tls_cfg_server_t config={};
    config.servercert_buf=(const unsigned char *)certificate; config.servercert_bytes=strlen(certificate)+1;
    config.serverkey_buf=(const unsigned char *)private_key; config.serverkey_bytes=strlen(private_key)+1;
    if (esp_tls_server_session_create(&config,client,tls)==0) {
        timeout={0,100000}; setsockopt(client,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));
        TerminalLink link; link.tls=tls;
        if (authenticate(link)) terminal_run(link);
    }
    esp_tls_server_session_delete(tls);
}
static bool raw_line(int fd, char *out, size_t capacity) {
    size_t count=0;
    while(count+1<capacity) {
        char c;
        if(recv(fd,&c,1,0)!=1) return false;
        if(c=='\n'){out[count]=0;return true;}
        out[count++]=c;
    }
    return false;
}
static bool raw_write(int fd, const char *data, size_t size) {
    while(size){int n=send(fd,data,size,0);if(n<=0)return false;data+=n;size-=n;}
    return true;
}
static void outbound(void *) {
    while(true) {
        if(!connected){relay_stage="waiting_wifi";vTaskDelay(pdMS_TO_TICKS(1000));continue;}
        relay_stage="connecting";
        addrinfo hints={}, *addresses=nullptr; hints.ai_family=AF_INET;hints.ai_socktype=SOCK_STREAM;
        char port[8];snprintf(port,sizeof(port),"%d",relay_port);
        int fd=-1;
        if(getaddrinfo(relay_host,port,&hints,&addresses)==0){
            fd=socket(AF_INET,SOCK_STREAM,IPPROTO_IP);
            if(fd>=0){
                timeval timeout={8,0};setsockopt(fd,SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof(timeout));
                setsockopt(fd,SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof(timeout));
                if(connect(fd,addresses->ai_addr,addresses->ai_addrlen)!=0){close(fd);fd=-1;}
            }
            freeaddrinfo(addresses);
        }
        if(fd>=0){
            char nonce[80], message[128], response[80];
            unsigned char key[32], digest[32];
            for(int i=0;i<32;i++){unsigned value=0;sscanf(relay_key+i*2,"%2x",&value);key[i]=(unsigned char)value;}
            bool valid=raw_line(fd,nonce,sizeof(nonce)) && strlen(nonce)==64 && strspn(nonce,"0123456789abcdef")==64;
            if(valid){
                snprintf(message,sizeof(message),"TraceSoul relay v1\ndevice\n%s",nonce);
                valid=mbedtls_md_hmac(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256),key,sizeof(key),(const unsigned char *)message,strlen(message),digest)==0;
                strcpy(response,"device ");
                for(int i=0;i<32;i++)snprintf(response+7+i*2,3,"%02x",digest[i]);
                strcat(response,"\n");
                valid=valid && raw_write(fd,response,strlen(response));
            }
            memset(key,0,sizeof(key));memset(digest,0,sizeof(digest));
            relay_stage="waiting_controller";
            int64_t until=esp_timer_get_time()+70000000;
            while(valid && connected && esp_timer_get_time()<until){
                valid=raw_line(fd,response,sizeof(response));
                if(valid && !strcmp(response,"GO")){relay_stage="session";serve_client(fd);break;}
                if(valid && strcmp(response,"WAIT"))break;
            }
            close(fd);
        }
        relay_stage="reconnecting";
        vTaskDelay(pdMS_TO_TICKS(250));
    }
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
        serve_client(client);
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
    if(nvs_open("tracesoul",NVS_READONLY,&handle)==ESP_OK){
        size=sizeof(data)-1;err=nvs_get_blob(handle,"relay",data,&size);nvs_close(handle);
        if(err==ESP_OK){
            json=cJSON_ParseWithLength(data,size);
            if(!json || !relay_validate(json,relay_host,relay_key,relay_port))relay_host[0]=0;
            cJSON_Delete(json);memset(data,0,sizeof(data));
        }
    }
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
    if(relay_host[0]) xTaskCreate(outbound,"mobile-terminal",16384,nullptr,4,nullptr);
}
