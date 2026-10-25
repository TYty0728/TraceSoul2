#pragma once
#include <stddef.h>
#include <stdint.h>
#include <cstring>

// Validation is independent of the screen. Never replace a good scene partially.
struct NativeScene {
    unsigned count=0,phase=0,interval=6500;
    size_t offset[128]{},length[128]{};
    unsigned codec[128]{};
};
inline uint32_t scene_u32(const uint8_t *p){return p[0]|uint32_t(p[1])<<8|uint32_t(p[2])<<16|uint32_t(p[3])<<24;}
inline bool scene_region(const uint8_t *p,size_t n,unsigned codec,size_t pixels,uint8_t *out=nullptr){
    if(codec==0){if(n!=pixels*2)return false;if(out)memcpy(out,p,n);return true;}
    if(codec!=1||n%4)return false;
    size_t written=0;
    for(size_t i=0;i<n;i+=4){
        unsigned run=p[i]|unsigned(p[i+1])<<8;
        if(!run||run>pixels-written)return false;
        if(out)for(unsigned j=0;j<run;j++){out[(written+j)*2]=p[i+2];out[(written+j)*2+1]=p[i+3];}
        written+=run;
    }
    return written==pixels;
}
inline bool scene_parse(const uint8_t *p,size_t n,NativeScene &scene){
    if(n<12||memcmp(p,"SCN1",4)||p[7])return false;
    scene.count=p[4]|unsigned(p[5])<<8;scene.phase=p[6];scene.interval=scene_u32(p+8);
    if(!scene.count||scene.count>128||scene.phase>7||scene.interval<2500||scene.interval>30000)return false;
    size_t pos=12;
    for(unsigned i=0;i<scene.count;i++){
        if(n-pos<5)return false;
        scene.codec[i]=p[pos];scene.length[i]=scene_u32(p+pos+1);pos+=5;scene.offset[i]=pos;
        if(scene.length[i]>n-pos||!scene_region(p+pos,scene.length[i],scene.codec[i],320*(i?163:480)))return false;
        pos+=scene.length[i];
    }
    return pos==n;
}
