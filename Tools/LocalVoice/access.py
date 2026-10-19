"""Private TTS credentials and short-lived, single-use browser launch tickets."""
import csv
import hmac
import io
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import threading
import time
import urllib.request

KEY = re.compile(r'^[A-Za-z0-9_-]{64}$')
COOKIE = 'tracesoul_voice_session'


def private_opener():
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self,*args,**kwargs):
            return None
    return urllib.request.build_opener(urllib.request.ProxyHandler({}),NoRedirect())


def protect_file(path):
    if os.name == 'nt':
        row = next(csv.reader(io.StringIO(subprocess.check_output(
            ['whoami','/user','/fo','csv','/nh'],text=True,creationflags=subprocess.CREATE_NO_WINDOW).strip())))
        result = subprocess.run(['icacls',str(path),'/inheritance:r','/grant:r',
                                 '*'+row[1]+':(F)','*S-1-5-18:(F)'],
                                stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise RuntimeError('Unable to restrict voice credential permissions.')
    else:
        os.chmod(path,0o600)


def write_private_json(path, value):
    path=Path(path)
    path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_name(path.name+'.'+secrets.token_hex(8)+'.tmp')
    try:
        with temporary.open('xb') as output:
            protect_file(temporary)
            output.write(json.dumps(value,ensure_ascii=False).encode())
            output.flush();os.fsync(output.fileno())
        os.replace(temporary,path)
    finally:
        temporary.unlink(missing_ok=True)


def load_key(runtime):
    path=Path(runtime)/'access.json'
    if not path.exists():
        write_private_json(path,{'api_key':secrets.token_urlsafe(48)})
    else:
        protect_file(path)
    try:
        value=json.loads(path.read_text(encoding='utf-8'))['api_key']
        if not isinstance(value,str) or not KEY.fullmatch(value):
            raise ValueError()
        return value
    except (OSError,ValueError,KeyError,TypeError):
        raise RuntimeError('Voice credentials are invalid; service remains closed.') from None


class VoiceAccess:
    def __init__(self,key,clock=time.monotonic):
        self.key,self.clock=key,clock
        self.tickets,self.sessions={},{}
        self.lock=threading.Lock()

    def bearer(self,authorization):
        prefix,value=(authorization or '').partition(' ')[::2]
        return prefix.lower()=='bearer' and bool(value) and hmac.compare_digest(value.encode(),self.key.encode())

    def allowed(self,request):
        authorization=request.headers.get('authorization')
        if authorization is not None:
            return self.bearer(authorization)
        value=request.cookies.get(COOKIE,'')
        with self.lock:
            return self.sessions.get(value,0)>self.clock()

    def ticket(self):
        with self.lock:
            now=self.clock()
            self.tickets={key:end for key,end in self.tickets.items() if end>now}
            if len(self.tickets)>=64:
                raise RuntimeError('Too many pending voice login links.')
            value=secrets.token_urlsafe(32)
            self.tickets[value]=now+60
            return value

    def session(self,ticket):
        with self.lock:
            now=self.clock()
            if self.tickets.pop(ticket,0)<=now:
                return None
            self.sessions={key:end for key,end in self.sessions.items() if end>now}
            if len(self.sessions)>=64:
                return None
            value=secrets.token_urlsafe(32)
            self.sessions[value]=now+8*3600
            return value


LOGIN_HTML='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>本地语音 · 需要验证</title><style>body{max-width:560px;margin:12vh auto;padding:24px;font:16px/1.8 system-ui;background:#f3f5f4;color:#21392f}input,button{font:inherit;padding:12px;box-sizing:border-box}input{width:100%}button{margin-top:16px}p{white-space:pre-wrap}</style>
<h1>本地语音需要验证</h1><p>从 TraceSoul2 的「本地语音工作台」入口或双击本地TTS启动器打开，可以自动验证。也可以在此输入本服务的 API Key。</p>
<form id="login"><label for="key">本地TTS API Key</label><input id="key" type="password" autocomplete="off" required><button>验证并打开</button></form><p id="state" role="status"></p>
<script>document.getElementById('login').onsubmit=async e=>{e.preventDefault();const field=document.getElementById('key');try{const r=await fetch('/auth/ticket',{method:'POST',headers:{Authorization:'Bearer '+field.value}});field.value='';if(!r.ok)throw Error('密钥无效或连接失败');const d=await r.json();location.replace('/auth/launch?ticket='+encodeURIComponent(d.ticket))}catch(e){document.getElementById('state').textContent=e.message}};</script></html>'''


def attach_access(app,backend):
    from fastapi import Request
    from fastapi.responses import HTMLResponse,JSONResponse,RedirectResponse,Response
    access=VoiceAccess(load_key(backend.runtime))

    @app.middleware('http')
    async def authenticate(request:Request,call_next):
        # Explicit credentials are required before parsing request bodies or invoking the model.
        path=request.url.path
        origin=request.headers.get('origin')
        if origin and origin!=f"{request.url.scheme}://{request.headers.get('host')}":
            return Response(status_code=403)
        if path=='/auth/launch' and request.method=='GET':
            response=await call_next(request)
        elif path=='/auth/ticket' and request.method=='POST':
            if not access.bearer(request.headers.get('authorization')):
                return JSONResponse({'detail':'unauthorized'},401,headers={'WWW-Authenticate':'Bearer','Cache-Control':'no-store'})
            response=await call_next(request)
        elif not (access.bearer(request.headers.get('authorization')) if path.startswith('/v1/') else access.allowed(request)):
            if path=='/' and request.method=='GET':
                response=HTMLResponse(LOGIN_HTML)
            elif path=='/health' and request.method=='GET':
                response=JSONResponse({'service':'TraceSoul2LocalVoice','ready':backend.model is not None,'authentication_required':True})
            else:
                response=JSONResponse({'detail':'unauthorized'},401,headers={'WWW-Authenticate':'Bearer'})
        else:
            response=await call_next(request)
        response.headers['Cache-Control']='no-store'
        response.headers['Referrer-Policy']='no-referrer'
        response.headers['X-Content-Type-Options']='nosniff'
        response.headers['X-Frame-Options']='DENY'
        return response

    @app.post('/auth/ticket')
    def create_ticket():
        try:
            return {'ticket':access.ticket(),'expires_in':60}
        except RuntimeError:
            return JSONResponse({'detail':'too_many_login_links'},429)

    @app.get('/auth/launch')
    def redeem(request:Request,ticket:str=''):
        session=access.session(ticket)
        if not session:
            return HTMLResponse('登录链接已失效，请从启动器或 TraceSoul2 重新打开。',status_code=401)
        response=RedirectResponse('/',status_code=303)
        response.set_cookie(COOKIE,session,httponly=True,samesite='strict',secure=request.url.scheme=='https',max_age=8*3600,path='/')
        return response
