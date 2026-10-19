import json
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from types import SimpleNamespace
import bridge


class StreamLink:
    def __init__(self, corrupt=False):
        self.corrupt = corrupt; self.lines = []; self.closed = False
        self.data = b'\x10\0' * 1536; self.offset = 0; self.total = 0; self.acks = []
    def next(self):
        self.total += len(self.data); self.offset = 0
        self.lines.append(dict(id=self.id, protocol=1, status='samples', received=self.total,
                               size=3072, sample_rate=48000, checksum=bridge.checksum(self.data)+self.corrupt))
    def write(self, data):
        value = json.loads(data)
        if value.get('op') == 'listen':
            self.id = value['id']; self.lines.append(dict(id=self.id, protocol=1, status='ready')); self.next()
        else:
            assert value['ack'] == self.total
            self.acks.append(value)
            if value['stop']: self.lines.append(dict(id=self.id, protocol=1, status='done', received=self.total))
            else: self.next()
    def read_until(self, *_): return json.dumps(self.lines.pop(0)).encode()+b'\n'
    def read(self, size):
        result = self.data[self.offset:self.offset+size]; self.offset += len(result); return result
    def close(self): self.closed = True


class Utterance:
    reason = None
    pcm = b''
    started_at = None
    frames = 0
    def __init__(self): self.count = 0
    def feed(self, block):
        self.count += 1
        if self.count == 3: self.reason = 'speech_end'


class StreamChecks(unittest.TestCase):
    def test_fixed_capture_is_complete_and_bounded(self):
        for duration, chunks in ((8000, 250), (15000, 469)):
            board = bridge.Board('fake'); board.connection = link = StreamLink()
            progress=[]
            pcm, receipt = (board.record_stream(on_progress=progress.append) if duration == 8000
                            else board.record_stream(duration, on_progress=progress.append))
            self.assertEqual(len(pcm), duration * 96)
            self.assertEqual(receipt['received'], chunks * 3072)
            self.assertTrue(link.acks[-1]['stop'])
            self.assertEqual(progress[0],32); self.assertEqual(progress[-1],duration)
        for duration in (True, '15000', 0, 18001):
            with self.assertRaises(ValueError): board.record_stream(duration)

    def test_progress_is_readable_during_capture_and_cleared_afterwards(self):
        import concurrent.futures
        entered=threading.Event(); finish=threading.Event()
        class FakeBoard:
            def record_stream(self, duration, event, on_progress):
                assert duration == 8000
                on_progress(640); entered.set()
                assert finish.wait(3)
                return bytes(96000), {'protocol':1,'status':'done','received':96000}
        handler=bridge.make_handler(FakeBoard(),'unused',SimpleNamespace(transcribe=lambda pcm:{'text':''}),SimpleNamespace(analyze=lambda pcm:(b'',[])),object())
        server=ThreadingHTTPServer(('127.0.0.1',0),handler)
        worker=threading.Thread(target=server.serve_forever,daemon=True);worker.start()
        def post(path):
            request=urllib.request.Request(f'http://127.0.0.1:{server.server_port}'+path,
                data=json.dumps({'listen_id':'b'*32}).encode(),headers={'Content-Type':'application/json'})
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request,timeout=5) as response:return json.load(response)
        try:
            with concurrent.futures.ThreadPoolExecutor() as pool:
                pending=pool.submit(post,'/api/voice/sample')
                self.assertTrue(entered.wait(2))
                state=post('/api/capture/state')
                self.assertEqual((state['phase'],state['recorded_ms']),('recording',640))
                finish.set(); pending.result(3)
                self.assertEqual(post('/api/capture/state')['phase'],'inactive')
        finally:finish.set();server.shutdown();server.server_close();worker.join(2)

    def test_validated_stream_stops_on_endpoint(self):
        board = bridge.Board('fake'); board.connection = link = StreamLink()
        receipt = board.listen(Utterance(), threading.Event())
        self.assertEqual(receipt['received'], 9216)
        self.assertEqual([x['stop'] for x in link.acks], [False, False, True])

    def test_cancelled_stream_stops_before_feeding_audio(self):
        board = bridge.Board('fake'); board.connection = link = StreamLink()
        event = threading.Event(); event.set(); utterance = Utterance()
        board.listen(utterance, event)
        self.assertEqual(utterance.reason, 'cancelled'); self.assertEqual(utterance.count, 0)

    def test_corrupt_stream_closes_without_ack_or_transcription(self):
        board = bridge.Board('fake'); board.connection = link = StreamLink(corrupt=True)
        with self.assertRaisesRegex(RuntimeError, '校验'): board.listen(Utterance(), threading.Event())
        self.assertTrue(link.closed); self.assertEqual(link.acks, [])

    def test_stop_before_register_is_not_lost(self):
        class FakeBoard:
            def listen(self, utterance, event):
                assert event.is_set()
                utterance.reason = 'cancelled'
                return {'protocol': 1, 'status': 'done', 'received': 3072}
        class FakeAsr:
            def transcribe(self, *_): raise AssertionError('Cancelled audio reached ASR')
        handler = bridge.make_handler(FakeBoard(), 'unused', FakeAsr(), SimpleNamespace(create=Utterance), object())
        server = ThreadingHTTPServer(('127.0.0.1', 0), handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True); worker.start()
        def post(path):
            request = urllib.request.Request(f'http://127.0.0.1:{server.server_port}'+path,
                data=json.dumps({'listen_id':'a'*32}).encode(), headers={'Content-Type':'application/json'})
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request, timeout=3) as response:
                return json.load(response)
        try:
            post('/api/listen/cancel')
            result = post('/api/utterance')
            self.assertEqual(result['status'], 'cancelled')
            self.assertEqual(result['text'], '')
            self.assertIsNone(result['voice'])
        finally: server.shutdown(); server.server_close(); worker.join(2)

    def test_short_name_reaches_asr_without_speaker_evidence(self):
        class FakeBoard:
            def listen(self, utterance, event):
                utterance.reason='speech_end'; utterance.pcm=b'\x10\0'*8000
                utterance.started_at=20; utterance.frames=65
                return {'protocol':1,'status':'done'}
        class FakeAsr:
            def transcribe(self, pcm, rate):
                self.assertion=(len(pcm),rate)
                return {'text':'小溪','status':'transcribed'}
        class FakeSpeaker:
            def evidence(self, pcm): return None
        asr=FakeAsr()
        handler=bridge.make_handler(FakeBoard(),'unused',asr,SimpleNamespace(create=Utterance),FakeSpeaker())
        server=ThreadingHTTPServer(('127.0.0.1',0),handler)
        worker=threading.Thread(target=server.serve_forever,daemon=True); worker.start()
        try:
            request=urllib.request.Request(f'http://127.0.0.1:{server.server_port}/api/utterance',
                data=json.dumps({'listen_id':'b'*32}).encode(),headers={'Content-Type':'application/json'})
            with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request,timeout=3) as response:
                result=json.load(response)
            self.assertEqual(result['text'],'小溪'); self.assertIsNone(result['voice'])
            self.assertEqual(asr.assertion,(16000,16000))
            self.assertEqual(result['started_ms'],640); self.assertEqual(result['recorded_ms'],2080)
        finally: server.shutdown(); server.server_close(); worker.join(2)
