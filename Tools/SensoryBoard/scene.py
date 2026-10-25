"""Bounded RGB565 scene: full first page, then caption strips, lossless RLE.

SCN1, page count u16le, phase u8, reserved u8, page interval u32le;
each region is codec u8 (0 raw, 1 runs), size u32le, then bytes.
Runs contain count u16le followed by a big endian RGB565 pixel.
"""
import struct

PHASES = ('standby', 'focused', 'listening', 'thinking', 'speaking', 'paused', 'offline', 'error')
CAPTION_TOP = 317


def pack_region(raw):
    packed = bytearray()
    start = 0
    while start < len(raw):
        pixel = raw[start:start+2]
        end = start + 2
        while end < len(raw) and end-start < 131070 and raw[end:end+2] == pixel:
            end += 2
        packed.extend(struct.pack('<H', (end-start)//2) + pixel)
        start = end
    codec, data = (1, packed) if len(packed) < len(raw) else (0, raw)
    return struct.pack('<BI', codec, len(data)) + data


def encode(frames, phase, page_ms=6500):
    if not 1 <= len(frames) <= 128 or not 2500 <= page_ms <= 30000:
        raise ValueError('无效的字幕场景')
    result = bytearray(struct.pack('<4sHBBI', b'SCN1', len(frames), PHASES.index(phase), 0, page_ms))
    for index, raw in enumerate(frames):
        if len(raw) != 320*480*2:
            raise ValueError('无效的画面尺寸')
        result.extend(pack_region(raw if index == 0 else raw[CAPTION_TOP*320*2:]))
    if len(result) > 960000:
        raise ValueError('字幕场景超过设备缓存上限')
    return bytes(result)
