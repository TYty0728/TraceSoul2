#pragma once
#include <stdint.h>

struct NativePose { int lift=0, sx=1000, sy=1000; };
// Twelve seconds: rest for 84%, then two small hops and settle.
inline NativePose native_hop_pose(uint32_t elapsed_ms){
    struct Key { unsigned ms; int lift,sx,sy; };
    static constexpr Key keys[]={{0,0,1000,1000},{10080,0,1000,1000},{10320,0,1016,984},
        {10680,-5,992,1008},{11040,0,1016,984},{11400,-4,994,1006},
        {11640,0,1008,992},{11880,0,1000,1000},{12000,0,1000,1000}};
    unsigned t=elapsed_ms%12000;
    for(unsigned i=1;i<sizeof(keys)/sizeof(keys[0]);i++)if(t<=keys[i].ms){
        const auto &a=keys[i-1],&b=keys[i];
        int p=(t-a.ms)*1000/(b.ms-a.ms);p=p*p*(3000-2*p)/1000000;
        return {a.lift+(b.lift-a.lift)*p/1000,a.sx+(b.sx-a.sx)*p/1000,a.sy+(b.sy-a.sy)*p/1000};
    }
    return {};
}
inline bool native_pose_equal(NativePose a,NativePose b){return a.lift==b.lift&&a.sx==b.sx&&a.sy==b.sy;}
