import random
import struct
import unittest
from scene import encode, pack_region, CAPTION_TOP


def unpack_region(blob):
    codec,size=struct.unpack_from('<BI',blob)
    data=blob[5:5+size]
    if codec==0:return data,5+size
    result=bytearray()
    for i in range(0,size,4):
        count=struct.unpack_from('<H',data,i)[0]
        result.extend(data[i+2:i+4]*count)
    return bytes(result),5+size


class SceneChecks(unittest.TestCase):
    def test_random_pixels_long_runs_and_caption_strips_roundtrip(self):
        rng=random.Random(14)
        for raw in (bytes(307200),rng.randbytes(307200),b'\x12\x34'*65536):
            packed=pack_region(raw)
            self.assertEqual(unpack_region(packed)[0],raw)
            self.assertLessEqual(len(packed),len(raw)+5)
        first=bytes(307200)
        second=b'\x13\x57'*153600
        packed=encode([first,second],'speaking',5000)
        self.assertEqual(struct.unpack_from('<4sHBBI',packed),(b'SCN1',2,4,0,5000))
        raw,n=unpack_region(packed[12:]);self.assertEqual(raw,first)
        raw,m=unpack_region(packed[12+n:]);self.assertEqual(raw,second[CAPTION_TOP*640:])
        self.assertEqual(12+n+m,len(packed))

    def test_invalid_scene_size_and_timing_rejected(self):
        for frames,interval in (([],5000),([bytes(2)],5000),([bytes(307200)],1)):
            with self.assertRaises(ValueError):encode(frames,'standby',interval)
