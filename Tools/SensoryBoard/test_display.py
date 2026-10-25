import base64
import io
import json
import threading
import unittest
import urllib.error
import urllib.request
import wave
from http.server import ThreadingHTTPServer

from PIL import Image
import bridge
from display import CompanionDisplay, STATES


class DisplayChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try: cls.font = bridge.choose_font()
        except ValueError: raise unittest.SkipTest('CJK font is required for screen rendering checks')

    def setUp(self):
        self.display = CompanionDisplay(self.font)

    def test_all_states_have_wire_size_and_no_reused_expression(self):
        digests = []
        for state in STATES:
            frame, result = self.display.render(dict(state=state,name='小汐',subtitle='嗯，我在呢。'))
            self.assertEqual(frame.size,(320,480))
            self.assertEqual(len(bridge.rgb565(frame)),307200)
            digests.append(frame.tobytes())
        self.assertEqual(len(set(digests)),len(STATES))

    def test_long_cjk_latin_and_newline_captions_preserve_content_across_pages(self):
        for text in ('这是一段很长的字幕，我们要检查标点和换行。'*20, 'abcdefghij0123456789 '*20,
                     '第一行\n\n第二行\n', '字'*500, '\n'*500):
            pages=self.display.pages(text)
            self.assertTrue(all(len(lines)<=4 for lines in pages))
            # Automatic line/page breaks add no characters and drop no source characters.
            self.assertEqual(''.join(''.join(page) for page in pages),text.replace('\n',''))
            self.assertEqual(sum(len(page) for page in pages)>=text.count('\n')+1,True)
        self.assertGreater(len(self.display.pages('字'*500)),1)
        frame,result=self.display.render(dict(subtitle='字'*500,page=1))
        self.assertEqual(result['page'],1)
        self.assertEqual(result['font_size'],20)

    def test_invalid_state_name_size_and_page_rejected(self):
        for request in ({'state':'unknown'},{'state':[]},{'name':' '},{'name':'a\nb'},
                        {'name':'字'*33},{'subtitle':'字'*501},{'subtitle':None},
                        {'remaining':31},{'remaining':True},{'page':1},{'page':-1},{'page':True}):
            with self.subTest(request=request), self.assertRaises(ValueError):self.display.render(request)

    def test_focus_frame_does_not_invent_a_frozen_thirty_second_timer(self):
        generic,_=self.display.render({'state':'focused'})
        thirty,_=self.display.render({'state':'focused','remaining':30})
        ten,_=self.display.render({'state':'focused','remaining':10})
        self.assertNotEqual(generic.tobytes(),thirty.tobytes())
        self.assertNotEqual(thirty.tobytes(),ten.tobytes())

    def service(self,board):
        server=ThreadingHTTPServer(('127.0.0.1',0),bridge.make_handler(board,self.font))
        worker=threading.Thread(target=server.serve_forever,daemon=True);worker.start()
        self.addCleanup(lambda:(server.shutdown(),server.server_close(),worker.join(2)))
        return f'http://127.0.0.1:{server.server_port}'

    def post(self,url,path,payload,headers=None):
        request=urllib.request.Request(url+path,data=json.dumps(payload).encode(),
            headers={'Content-Type':'application/json',**(headers or {})})
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request,timeout=5) as response:
            return json.load(response)

    def test_preview_never_contacts_board_and_actual_output_has_exact_preview_pixels(self):
        class Board:
            def __init__(self):self.calls=[]
            def transact(self,op,data):
                self.calls.append((op,data));return {'status':'done','protocol':1,'id':1}
        board=Board(); url=self.service(board)
        request=dict(state='listening',name='小汐',subtitle='这是一句模拟听写。')
        result=self.post(url,'/api/display/preview',request)
        self.assertEqual(board.calls,[])
        self.assertTrue(result['display']['preview_only'])
        sent=self.post(url,'/api/display',request)
        self.assertFalse(sent['display']['preview_only'])
        self.assertEqual(sent['preview'],result['preview'])
        with Image.open(io.BytesIO(base64.b64decode(result['preview']))) as image:
            from scene import encode
            self.assertEqual(board.calls,[('scene',encode([bridge.rgb565(image)],'listening'))])
        with self.assertRaises(urllib.error.HTTPError):self.post(url,'/api/display',dict(page=100))
        self.assertEqual(len(board.calls),1)

    def test_preview_only_process_cannot_record_play_or_send_even_if_called_directly(self):
        url=self.service(None)
        self.assertTrue(self.post(url,'/api/display/preview',{})['ok'])
        for path in ('/api/display','/api/status','/api/speak','/api/utterance','/api/text','/api/chime'):
            with self.assertRaises(urllib.error.HTTPError) as error:self.post(url,path,{})
            self.assertEqual(error.exception.code,409)
        with self.assertRaises(urllib.error.HTTPError) as error:
            self.post(url,'/api/display/preview',{},headers={'Origin':'https://outside.example'})
        self.assertEqual(error.exception.code,403)

    def test_shared_artwork_replaces_face_preserves_status_caption_and_alpha(self):
        output=io.BytesIO();Image.new('RGBA',(80,80),(255,0,0,255)).save(output,format='PNG')
        artwork=base64.b64encode(output.getvalue()).decode()
        baseline,_=self.display.render(dict(state='speaking',subtitle='这是一句字幕。'))
        frame,layout=self.display.render(dict(state='speaking',subtitle='这是一句字幕。',image=artwork))
        self.assertTrue(layout['artwork']);self.assertEqual(frame.getpixel((160,150)),(255,0,0))
        for bounds in ((0,0,320,65),(0,257,320,480)):
            self.assertEqual(frame.crop(bounds).tobytes(),baseline.crop(bounds).tobytes())
        for image in ('invalid',base64.b64encode(b'not an image').decode()):
            with self.assertRaises(ValueError):self.display.render(dict(image=image))
        transparent=io.BytesIO();Image.new('RGBA',(80,80),(255,0,0,0)).save(transparent,format='PNG')
        frame,_=self.display.render(dict(image=base64.b64encode(transparent.getvalue()).decode()))
        self.assertEqual(frame.getpixel((160,150)),(247,247,242))

    def test_only_actual_shared_artwork_opts_into_native_motion(self):
        class Board:
            def __init__(self):self.metadata=[]
            def ui(self,*args):return {'status':'done','protocol':1,'ui_pages':1}
            def transact(self,op,data,**metadata):
                self.metadata.append(metadata);return {'status':'done','protocol':1}
        board=Board();url=self.service(board)
        output=io.BytesIO();Image.new('RGB',(80,80),'red').save(output,format='PNG')
        self.post(url,'/api/display',dict(image=base64.b64encode(output.getvalue()).decode()))
        self.post(url,'/api/display',{})
        self.assertEqual(board.metadata,[{'artwork':True}])

    def test_speech_caches_all_caption_pages_and_one_whole_audio(self):
        class Board:
            def __init__(self):self.calls=[]
            def transact(self,op,data):self.calls.append((op,data));return {'status':'done','protocol':1}
        board=Board();url=self.service(board)
        out=io.BytesIO()
        with wave.open(out,'wb') as wav:
            wav.setnchannels(1);wav.setsampwidth(2);wav.setframerate(48000);wav.writeframes(b'\x11\x00'*48000*11)
        raw=out.getvalue();payload=dict(data=base64.b64encode(raw).decode(),display=dict(subtitle='测试字幕。'*20))
        result=self.post(url,'/api/speak',payload)
        frames=[data for op,data in board.calls if op=='scene'];audio=[data for op,data in board.calls if op=='audio_stream']
        self.assertEqual(len(frames),1)
        self.assertFalse(result['caption_preview'])
        self.assertGreater(result['caption_pages'],1)
        self.assertEqual(int.from_bytes(frames[0][4:6],'little'),result['caption_pages'])
        self.assertEqual(b''.join(audio),bridge.decode_wav(raw,max_seconds=120))
        self.assertEqual(len(audio),1)
        self.assertEqual(result['display_pages'],len(frames));self.assertTrue(result['display_complete'])
        before=len(board.calls);payload['display']['image']='invalid'
        with self.assertRaises(urllib.error.HTTPError):self.post(url,'/api/speak',payload)
        self.assertEqual(len(board.calls),before)


if __name__=='__main__':
    unittest.main()
