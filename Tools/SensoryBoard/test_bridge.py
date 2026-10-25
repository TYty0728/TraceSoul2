import io
import json
import struct
import unittest
import wave
import base64
import threading
import urllib.request
import urllib.error
from http.server import ThreadingHTTPServer

from PIL import Image
import bridge


class Link:
    def __init__(self, fail_at=None):
        self.replies = []
        self.header = None
        self.payload = bytearray()
        self.fail_at = fail_at
        self.closed = False

    def write(self, data):
        if self.header is None:
            self.header = json.loads(data)
            self.replies.append({'id': self.header['id'], 'protocol': 1, 'status': 'ready'})
        else:
            self.payload.extend(data)
            self.replies.append({'id': self.header['id'], 'protocol': 1, 'status': 'chunk',
                                 'received': len(self.payload) + (1 if self.fail_at else 0)})
            if len(self.payload) == self.header['size']:
                self.replies.append({'id': self.header['id'], 'protocol': 1, 'status': 'done',
                                     'received': len(self.payload)})

    def read_until(self, *_):
        return json.dumps(self.replies.pop(0)).encode() + b'\n'

    def close(self):
        self.closed = True


class BridgeChecks(unittest.TestCase):
    def test_timeout_split_json_receipt_is_reassembled_without_replaying(self):
        class SplitLink:
            def __init__(self):self.parts=iter([b'{"protocol":1,"id":7,',b'',b'"status":"done","received":20}\\n'])
            def read_until(self,*args):return next(self.parts).replace(b'\\n',b'\n')
        board=bridge.Board('fake');board.connection=SplitLink()
        self.assertEqual(board._reply(7,'done',20)['received'],20)

    def test_stream_packets_are_checked_and_one_final_receipt_completes_reply(self):
        class StreamLink(Link):
            def write(self, data):
                if self.header is None:
                    super().write(data)
                    self.replies[0]['audio_stream_version']=1
                else:
                    block=data[:-4]
                    self_test.assertEqual(struct.unpack('<I',data[-4:])[0],bridge.checksum(block))
                    super().write(block)
        self_test=self
        board=bridge.Board('fake');board.connection=link=StreamLink()
        pcm=b'\x11\0'*(48000*11)
        result=board.transact('audio_stream',pcm)
        self.assertEqual(link.header['op'],'audio_stream')
        self.assertEqual(bytes(link.payload),pcm)
        self.assertEqual(result['received'],len(pcm))
        self.assertEqual(result['status'],'done')

    def test_old_firmware_gets_no_stream_payload_and_no_segmented_fallback(self):
        board=bridge.Board('fake');board.connection=link=Link()
        with self.assertRaisesRegex(RuntimeError,'0.4.7'):
            board.transact('audio_stream',b'\0\0'*48000)
        self.assertEqual(link.payload,b'')
        self.assertTrue(link.closed)

    def test_external_tts_wav_is_one_continuous_playback_without_system_fallback(self):
        class FakeBoard:
            def __init__(self): self.chunks=[]
            def transact(self, operation, pcm):
                assert operation=='audio_stream'
                self.chunks.append(pcm)
                return {'id':len(self.chunks),'protocol':1,'status':'done'}
        output=io.BytesIO()
        with wave.open(output,'wb') as wav:
            wav.setnchannels(1);wav.setsampwidth(2);wav.setframerate(8000)
            wav.writeframes(b'\x10\0'*8000*11)
        board=FakeBoard()
        server=ThreadingHTTPServer(('127.0.0.1',0),bridge.make_handler(board,'unused'))
        worker=threading.Thread(target=server.serve_forever,daemon=True);worker.start()
        def post(data):
            request=urllib.request.Request(f'http://127.0.0.1:{server.server_port}/api/speak',
                data=json.dumps({'data':base64.b64encode(data).decode()}).encode(),headers={'Content-Type':'application/json'})
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request,timeout=5) as response:return json.load(response)
        try:
            result=post(output.getvalue())
            self.assertEqual([len(chunk) for chunk in board.chunks],[11*96000])
            self.assertEqual(result['audio_ms'],11000)
            self.assertEqual(len(result['receipts']),1)
            self.assertEqual(result['engine'],'外部角色语音')
            before=len(board.chunks)
            with self.assertRaises(urllib.error.HTTPError):post(b'invalid audio')
            self.assertEqual(len(board.chunks),before)
        finally:server.shutdown();server.server_close();worker.join(2)

    def test_rgb565_wire_order(self):
        image = Image.new('RGB', (320, 480), 'red')
        data = bridge.rgb565(image)
        self.assertEqual(len(data), 307200)
        self.assertEqual(data[:4], b'\xf8\x00\xf8\x00')

    def test_frame_dimensions_rejected(self):
        with self.assertRaises(ValueError):
            bridge.rgb565(Image.new('RGB', (10, 10)))

    def test_known_checksum(self):
        self.assertEqual(bridge.checksum(b'hello'), 0x4F9F2CAB)

    def test_complete_chunked_transfer(self):
        board = bridge.Board('fake')
        board.connection = link = Link()
        data = bytes(range(256)) * 33
        receipt = board.transact('audio', data)
        self.assertEqual(bytes(link.payload), data)
        self.assertEqual(link.header['checksum'], bridge.checksum(data))
        self.assertEqual(receipt['status'], 'done')

    def test_artwork_flag_is_explicit_and_never_applies_to_audio(self):
        board=bridge.Board('fake');board.connection=link=Link()
        board.transact('frame',b'\0'*307200,artwork=True)
        self.assertIs(link.header['artwork'],True)
        for operation,flag in (('audio',True),('frame',1)):
            with self.assertRaises(ValueError):board.transact(operation,b'',artwork=flag)

    def test_wrong_ack_closes_without_resending(self):
        board = bridge.Board('fake')
        board.connection = link = Link(fail_at=1)
        with self.assertRaisesRegex(RuntimeError, '字节数'):
            board.transact('audio', b'x' * 8192)
        self.assertEqual(len(link.payload), 4096)
        self.assertTrue(link.closed)
        self.assertIsNone(board.connection)

    def test_device_error_is_not_success(self):
        board = bridge.Board('fake')
        board.connection = link = Link()
        link.replies = [{'id': 7, 'protocol': 1, 'status': 'error', 'error': 'checksum_mismatch'}]
        with self.assertRaisesRegex(RuntimeError, 'checksum_mismatch'):
            board._reply(7, 'done')

    def test_stale_receipt_ignored(self):
        board = bridge.Board('fake')
        board.connection = link = Link()
        link.replies = [{'id': 3, 'protocol': 1, 'status': 'done'},
                        {'id': 7, 'protocol': 1, 'status': 'done'}]
        self.assertEqual(board._reply(7, 'done')['id'], 7)

    def test_wav_stereo_resampling(self):
        out = io.BytesIO()
        with wave.open(out, 'wb') as wav:
            wav.setnchannels(2); wav.setsampwidth(2); wav.setframerate(16000)
            wav.writeframes(struct.pack('<hhhh', 1000, 3000, 2000, 4000))
        pcm = bridge.decode_wav(out.getvalue())
        self.assertEqual(len(pcm), 12)
        self.assertEqual(struct.unpack_from('<h', pcm)[0], 2000)

    def test_long_audio_rejected(self):
        out = io.BytesIO()
        with wave.open(out, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(8000)
            wav.writeframes(b'\0\0' * 80001)
        with self.assertRaises(ValueError):
            bridge.decode_wav(out.getvalue())

    def test_text_overflow_rejected(self):
        with self.assertRaises(ValueError):
            bridge.text_frame('字' * 501, 'unused')


if __name__ == '__main__':
    unittest.main()
