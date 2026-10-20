#pragma once
#include <stddef.h>
#include <stdint.h>
struct DeviceControlState { bool online, active, starting, stopping, failed; unsigned phase; uint32_t revision; };
void device_control_init();
bool device_control_update(const char *controller,uint32_t ack,bool active,bool allowed,unsigned phase,bool failed);
void device_control_receipt(char *out,size_t size,uint32_t id);
DeviceControlState device_control_state();
bool device_control_start();
void device_control_stop();
void device_control_tick();
