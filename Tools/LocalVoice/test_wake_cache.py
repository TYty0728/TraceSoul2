import io
import json
from pathlib import Path
import tempfile
import unittest
import wave

from fastapi.testclient import TestClient
from access import load_key
from server import LocalVoice, SpeechRequest, VoiceFailure, create_app


class CachedVoice(LocalVoice):
    def __init__(self, root):
        super().__init__(root)
        self.model = object()
        self.model_stamp = 'test-model-v1'
        self.calls = 0
        self.action = None

    def _synthesize_locked(self, request):
        self.calls += 1
        if self.action:
            self.action()
        output = io.BytesIO()
        with wave.open(output, 'wb') as writer:
            writer.setnchannels(1); writer.setsampwidth(2); writer.setframerate(16000)
            writer.writeframes(b'\x10\0' * 16000)
        return output.getvalue(), dict(synthesis_ms=100, audio_ms=1000, rtf=0.1)


class WakeCacheChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        for voice in ('character', 'alternate'):
            folder = self.root / 'voices' / voice
            folder.mkdir(parents=True)
            (folder / 'reference.wav').write_bytes(b'public reference')
            (folder / 'reference.txt').write_text('公开参考语句', encoding='utf-8')
        self.voice = CachedVoice(self.root)
        self.cache = self.voice.wake_cache
        self.client = TestClient(create_app(self.voice, load_model=False), base_url='http://127.0.0.1')
        self.key = load_key(self.root)
        self.headers = {'Authorization': 'Bearer ' + self.key, 'X-TraceSoul-Wake-Cache': 'v1'}

    def tearDown(self):
        self.client.close()
        self.temp.cleanup()

    def post(self, text='诶，我在。', **fields):
        return self.client.post('/v1/audio/speech', headers=self.headers, json=dict(input=text, **fields))

    def test_miss_hit_persist_and_uncached_normal_dialogue(self):
        first, second = self.post(), self.post()
        self.assertEqual(first.status_code, 200)
        self.assertEqual(first.headers['x-tracesoul-wake-cache'], 'miss')
        self.assertEqual(second.headers['x-tracesoul-wake-cache'], 'hit')
        self.assertEqual(second.headers['x-synthesis-ms'], '0')
        self.assertEqual(first.content, second.content)
        self.assertEqual(self.voice.calls, 1)
        restarted = CachedVoice(self.root)
        self.assertTrue(restarted.wake_cache.synthesize(SpeechRequest(input='诶，我在。'))[2])
        self.assertEqual(restarted.calls, 0)
        normal = self.client.post('/v1/audio/speech', headers={'Authorization':'Bearer '+self.key}, json={'input':'诶，我在。'})
        self.assertNotIn('x-tracesoul-wake-cache', normal.headers)
        self.assertEqual(self.voice.calls, 2)

    def test_authentication_still_required_on_hit_and_no_public_cache_route(self):
        self.post()
        for auth in ({}, {'Authorization':'Bearer wrong'}):
            response = self.client.post('/v1/audio/speech', json={'input':'诶，我在。'},
                headers={**auth, 'X-TraceSoul-Wake-Cache':'v1'})
            self.assertEqual(response.status_code, 401)
        ticket = self.client.post('/auth/ticket', headers=self.headers).json()['ticket']
        self.client.get('/auth/launch', params={'ticket':ticket}, follow_redirects=False)
        self.assertEqual(self.client.post('/v1/audio/speech',json={'input':'诶，我在。'},
            headers={'X-TraceSoul-Wake-Cache':'v1'}).status_code, 401)
        self.assertNotEqual(self.client.get('/cache/wake-audio/test.json', headers=self.headers).status_code, 200)
        self.assertEqual(self.voice.calls, 1)
        health=self.client.get('/health').text
        self.assertNotIn(self.key, health); self.assertNotIn(str(self.root), health)

    def test_text_parameters_reference_content_model_and_active_selection_invalidate(self):
        self.post()
        self.assertEqual(self.post('我在听。').headers['x-tracesoul-wake-cache'], 'miss')
        self.assertEqual(self.post(seed=1).headers['x-tracesoul-wake-cache'], 'miss')
        self.assertEqual(self.post(temperature=0.7).headers['x-tracesoul-wake-cache'], 'miss')
        ref = self.root/'voices'/'character'
        (ref/'reference.wav').write_bytes(b'changed reference')
        self.assertEqual(self.post().headers['x-tracesoul-wake-cache'], 'miss')
        (ref/'reference.txt').write_text('更换参考语句',encoding='utf-8')
        self.assertEqual(self.post().headers['x-tracesoul-wake-cache'], 'miss')
        self.voice.model_stamp='test-model-v2'
        self.assertEqual(self.post().headers['x-tracesoul-wake-cache'], 'miss')
        (ref/'active.json').write_text(json.dumps({'voice':'alternate'}),encoding='utf-8')
        self.assertEqual(self.post().headers['x-tracesoul-wake-cache'], 'miss')
        self.assertEqual(self.voice.calls, 8)

    def test_hit_works_while_gpu_busy_but_miss_does_not_queue_or_retry(self):
        self.post(); self.voice.lock.acquire()
        try:
            self.assertEqual(self.post().headers['x-tracesoul-wake-cache'], 'hit')
            self.assertEqual(self.post('另一句').status_code, 429)
            self.assertEqual(self.voice.calls, 1)
        finally:
            self.voice.lock.release()

    def test_corrupt_entries_rebuild_and_bounds_prune_only_owned_files(self):
        self.post()
        entry=next(self.cache.folder.glob('*.json')); entry.write_text('{corrupt')
        self.assertEqual(self.post().headers['x-tracesoul-wake-cache'], 'miss')
        other=self.cache.folder/'keep.json'; other.write_text('keep')
        for n in range(20):
            self.assertEqual(self.post('测试'+str(n)).status_code, 200)
        entries=[p for p in self.cache.folder.glob('*.json') if len(p.stem)==64]
        self.assertEqual(len(entries),16)
        self.assertTrue(other.exists())
        self.assertEqual(list(self.cache.folder.glob('*.tmp')),[])
        self.assertNotIn('诶，我在', entries[0].read_text())

    def test_failure_and_voice_change_never_cache_audio_or_retry(self):
        def change():
            (self.root/'voices'/'character'/'reference.txt').write_text('参考改变',encoding='utf-8')
        self.voice.action=change
        response=self.post()
        self.assertEqual(response.status_code,400)
        self.assertEqual(response.json()['detail'],'voice_changed_during_generation')
        self.assertEqual(self.voice.calls,1)
        self.assertFalse(self.cache.folder.exists())
        self.voice.action=lambda: (_ for _ in ()).throw(VoiceFailure('model_not_ready'))
        self.assertEqual(self.post().status_code,503)
        self.assertEqual(self.voice.calls,2)
        self.assertFalse(self.voice.lock.locked())

    def test_malformed_protocol_long_text_and_invalid_wav_never_cached(self):
        self.assertEqual(self.post('字'*31).status_code,400)
        self.assertEqual(self.client.post('/v1/audio/speech',json={'input':'我在'},
            headers={**self.headers,'X-TraceSoul-Wake-Cache':'v2'}).status_code,400)
        self.assertEqual(self.voice.calls,0)
        for invalid in (b'RIFF'+bytes(100), self.voice._synthesize_locked(SpeechRequest(input='我在'))[0][:-10]):
            with self.assertRaises(VoiceFailure):self.cache.audio_ms(invalid)


if __name__ == '__main__':
    unittest.main()
