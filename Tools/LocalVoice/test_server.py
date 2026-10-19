import json
from pathlib import Path
import tempfile
import unittest
import io
import wave
from fastapi.testclient import TestClient
from server import LocalVoice, VoiceFailure, create_app
from access import load_key, COOKIE, VoiceAccess


class FakeVoice(LocalVoice):
    def __init__(self, root):
        super().__init__(root)
        self.model = object()
        self.calls = 0

    def synthesize(self, request):
        self.calls += 1
        self.reference(request.voice)
        return b"RIFF" + bytes(1000), {"synthesis_ms": 500, "audio_ms": 3000, "rtf": 0.167}


class ServiceChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.voice = FakeVoice(self.root)
        self.client = TestClient(create_app(self.voice, load_model=False), base_url="http://127.0.0.1")
        self.client.headers['Authorization']='Bearer '+load_key(self.root)

    def tearDown(self):
        self.client.close()
        self.temporary.cleanup()

    def test_missing_character_does_not_use_demo(self):
        response = self.client.post('/v1/audio/speech', json={"input": "你好"})
        self.assertEqual(response.status_code, 400)
        self.assertEqual(response.json()["detail"], "voice_reference_missing")
        self.assertEqual(self.client.get('/health').json()["voices"][0]["ready"], False)

    def test_reference_becomes_available_without_restart(self):
        folder = self.root / 'voices' / 'character'
        folder.mkdir(parents=True)
        (folder / 'reference.wav').write_bytes(b'RIFF')
        (folder / 'reference.txt').write_text('示例文字', encoding='utf-8')
        self.assertTrue(self.client.get('/health').json()["voices"][0]["ready"])
        response = self.client.post('/v1/audio/speech', json={"input": "你好"})
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.headers['x-synthesis-ms'], '500')
        self.assertTrue(response.content.startswith(b'RIFF'))
        self.assertEqual(response.headers['cache-control'], 'no-store')

    def test_cross_origin_and_host_rejected(self):
        self.assertEqual(self.client.post('/v1/audio/speech', json={"input": "你好"},
            headers={"Origin": "https://example.com"}).status_code, 403)
        self.assertEqual(self.client.get('/health', headers={"Host": "example.com"}).status_code, 400)
        self.assertEqual(self.voice.calls, 0)

    def test_validation_does_not_call_model(self):
        for payload in ({"input": " "}, {"input": "x" * 241}, {"input": "hi", "response_format": "mp3"},
                        {"input": "hi", "model": "other"}, {"input": "hi", "language": "unknown"},
                        {"input":"hi","seed":-1}, {"input":"hi","temperature":2}, {"input":"hi","top_p":0.1}):
            self.assertGreaterEqual(self.client.post('/v1/audio/speech', json=payload).status_code, 400)
        self.assertEqual(self.voice.calls, 0)

    def test_no_reference_path_traversal(self):
        for voice in ('../private', '/etc', 'file://demo', ''):
            with self.assertRaises(VoiceFailure):
                self.voice.reference(voice)

    def test_no_generated_text_or_file_paths_in_health(self):
        data = self.client.get('/health').json()
        self.assertFalse(data['streaming'])
        self.assertNotIn(str(self.root), json.dumps(data))
        self.assertEqual(self.client.get('/').status_code, 200)


class LabVoice(FakeVoice):
    seconds = 4

    def synthesize(self, request):
        self.calls += 1
        self.reference(request.voice)
        output=io.BytesIO()
        with wave.open(output,'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(16000)
            wav.writeframes(b'\x10\0'*16000*self.seconds)
        return output.getvalue(),dict(synthesis_ms=400,audio_ms=self.seconds*1000,rtf=0.1)


class LabChecks(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory();self.root=Path(self.temporary.name)
        for voice in ('character','source_remote'):
            folder=self.root/'voices'/voice;folder.mkdir(parents=True)
            (folder/'reference.wav').write_bytes(b'original reference')
            (folder/'reference.txt').write_text('原始参考对白',encoding='utf-8')
        self.voice=LabVoice(self.root)
        self.client=TestClient(create_app(self.voice,load_model=False),base_url='http://127.0.0.1')
        self.client.headers['Authorization']='Bearer '+load_key(self.root)

    def tearDown(self):
        self.client.close();self.temporary.cleanup()

    def card(self):
        response=self.client.post('/lab/cards',json=dict(text='这是统一试听文字',voice='source_remote',seed=17,temperature=0.8,top_p=0.95))
        self.assertEqual(response.status_code,200)
        return response.json()

    def test_generate_persist_preview_without_changing_active_voice(self):
        card=self.card()
        self.assertEqual(self.client.get('/lab/cards').json()['cards'][0]['seed'],17)
        self.assertIsNone(self.client.get('/lab/cards').json()['active_card'])
        self.assertTrue(self.client.get(card['audio_url']).content.startswith(b'RIFF'))
        self.assertEqual((self.root/'voices'/'character'/'reference.wav').read_bytes(),b'original reference')
        self.assertNotIn(str(self.root),json.dumps(card))

    def test_select_changes_character_reference_and_restore_preserves_candidates(self):
        card=self.card()
        selected=self.client.post('/lab/activate',json={'id':card['id']})
        self.assertEqual(selected.status_code,200)
        audio,text,stamp=self.voice.reference('character')
        self.assertEqual(text,'这是统一试听文字')
        self.assertTrue(audio.read_bytes().startswith(b'RIFF'))
        self.assertEqual(self.client.get('/lab/cards').json()['active_card'],card['id'])
        self.assertEqual(self.client.post('/lab/restore',json={}).status_code,200)
        self.assertEqual(self.voice.reference('character')[0].read_bytes(),b'original reference')
        self.assertEqual(len(self.client.get('/lab/cards').json()['cards']),1)
        self.assertEqual((self.root/'voices'/'source_remote'/'reference.wav').read_bytes(),b'original reference')

    def test_custom_text_preview_does_not_activate_or_overwrite_reference(self):
        card=self.card()
        response=self.client.post('/lab/cards/'+card['id']+'/preview',json={'text':'这次读我输入的文字'})
        self.assertEqual(response.status_code,200)
        self.assertEqual(response.headers['x-audio-ms'],'4000')
        self.assertTrue(response.content.startswith(b'RIFF'))
        self.assertIsNone(self.client.get('/lab/cards').json()['active_card'])
        self.assertEqual((self.root/'voices'/'character'/'reference.wav').read_bytes(),b'original reference')
        self.assertEqual(self.client.get('/lab/original/audio').content,b'original reference')
        before=self.voice.calls
        self.assertEqual(self.client.post('/lab/cards/'+card['id']+'/preview',json={'text':' '}).status_code,400)
        self.assertEqual(self.client.post('/lab/cards/'+card['id']+'/preview',json={'text':'x'*241}).status_code,422)
        self.assertEqual(self.voice.calls,before)

    def test_invalid_parameters_and_paths_do_not_call_model(self):
        for fields in ({'seed':-1},{'seed':1,'voice':'../private'},{'seed':1,'temperature':2}):
            self.assertEqual(self.client.post('/lab/cards',json=dict(text='测试',**fields)).status_code,422)
        self.assertEqual(self.voice.calls,0)
        self.assertEqual(self.client.post('/lab/activate',json={'id':'../private'}).status_code,422)
        self.assertEqual(self.client.get('/lab/cards/'+'a'*32+'/audio').status_code,404)

    def test_busy_or_short_selection_keeps_current_reference(self):
        card=self.card();self.voice.lock.acquire()
        try:self.assertEqual(self.client.post('/lab/activate',json={'id':card['id']}).status_code,409)
        finally:self.voice.lock.release()
        self.voice.seconds=1;short=self.card()
        self.assertEqual(self.client.post('/lab/activate',json={'id':short['id']}).status_code,400)
        self.assertIsNone(self.client.get('/lab/cards').json()['active_card'])

    def test_character_pointer_cannot_escape_voice_directory(self):
        (self.root/'voices'/'character'/'active.json').write_text(json.dumps({'voice':'../private'}),encoding='utf-8')
        with self.assertRaises(VoiceFailure):self.voice.reference('character')


class AuthChecks(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory();self.root=Path(self.temporary.name)
        self.voice=FakeVoice(self.root)
        self.client=TestClient(create_app(self.voice,load_model=False),base_url='http://127.0.0.1')
        self.key=load_key(self.root)
        self.headers={'Authorization':'Bearer '+self.key}

    def tearDown(self):
        self.client.close();self.temporary.cleanup()

    def test_missing_wrong_or_query_key_cannot_reach_model_or_validation(self):
        for headers in ({},{'Authorization':'Bearer wrong'},{'Authorization':'Basic '+self.key}):
            response=self.client.post('/v1/audio/speech',content=b'not json',headers=headers)
            self.assertEqual(response.status_code,401)
            self.assertEqual(response.headers['www-authenticate'],'Bearer')
            self.assertNotIn(self.key,response.text)
        self.assertEqual(self.client.get('/v1/models?api_key='+self.key).status_code,401)
        self.assertEqual(self.voice.calls,0)

    def test_public_health_and_login_do_not_expose_voice_or_credentials(self):
        health=self.client.get('/health').json()
        self.assertTrue(health['authentication_required'])
        for field in ('voices','model','last_timing'):
            self.assertNotIn(field,health)
        login=self.client.get('/')
        self.assertNotIn(self.key,login.text)
        self.assertNotIn(str(self.root),login.text)
        self.assertIn('type="password"',login.text)
        self.assertEqual(login.headers['cache-control'],'no-store')

    def test_all_voice_and_lab_routes_require_auth(self):
        for path in ('/lab/cards','/lab/original/audio','/lab/cards/'+'a'*32+'/audio','/v1/models','/openapi.json'):
            self.assertEqual(self.client.get(path).status_code,401,path)
        for path in ('/lab/cards','/lab/restore','/lab/activate','/ui/speech','/auth/ticket'):
            self.assertEqual(self.client.post(path,json={}).status_code,401,path)
        self.assertEqual(self.voice.calls,0)

    def test_browser_ticket_is_single_use_and_does_not_authorize_api_without_key(self):
        ticket=self.client.post('/auth/ticket',headers=self.headers).json()['ticket']
        response=self.client.get('/auth/launch',params={'ticket':ticket},follow_redirects=False)
        self.assertEqual(response.status_code,303)
        self.assertIn('HttpOnly',response.headers['set-cookie'])
        self.assertIn('SameSite=strict',response.headers['set-cookie'])
        self.assertIn(COOKIE,self.client.cookies)
        self.assertEqual(self.client.get('/auth/launch',params={'ticket':ticket}).status_code,401)
        self.assertEqual(self.client.get('/lab/cards').status_code,200)
        self.assertEqual(self.client.get('/v1/models').status_code,401)
        self.assertEqual(self.client.get('/v1/models',headers=self.headers).status_code,200)
        self.assertEqual(self.client.get('/lab/cards',headers={'Authorization':'Bearer wrong'}).status_code,401)
        self.assertEqual(self.client.get('/lab/cards',headers={'Origin':'https://evil.example'}).status_code,403)
        folder=self.root/'voices'/'character';folder.mkdir(parents=True)
        (folder/'reference.wav').write_bytes(b'RIFF');(folder/'reference.txt').write_text('test')
        self.assertEqual(self.client.post('/ui/speech',json={'input':'test'}).status_code,200)

    def test_ticket_expiry_and_session_expiry(self):
        now=[10.0];access=VoiceAccess(self.key,clock=lambda:now[0])
        ticket=access.ticket();now[0]+=61
        self.assertIsNone(access.session(ticket))
        ticket=access.ticket();session=access.session(ticket)
        self.assertIsNotNone(session)
        self.assertIsNone(access.session(ticket))
        class Request:
            headers={}
            cookies={COOKIE:session}
        self.assertTrue(access.allowed(Request()))
        now[0]+=8*3600+1
        self.assertFalse(access.allowed(Request()))

    def test_key_persists_and_invalid_config_does_not_reset_it(self):
        self.assertEqual(load_key(self.root),self.key)
        path=self.root/'access.json';path.write_text('{bad',encoding='utf-8')
        with self.assertRaises(RuntimeError):load_key(self.root)
        self.assertEqual(path.read_text(encoding='utf-8'),'{bad')


if __name__ == '__main__':
    unittest.main()
