"""Identity reorganization through an isolated Host with mock inference and synthetic sources."""
import base64
import hashlib
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

calls = []


class Upstream(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        calls.append((self.path, body))
        messages = body['messages']
        if '理解归并' in messages[0]['content']:
            ids = [x['Id'] for x in json.loads(messages[1]['content'])['cognitions']]
            content = {'understandings': [{'meaning': '在短暂分开后恢复连接', 'scope': '双方可回应时',
                'exceptions': '忙碌时需要沟通', 'certainty': '逐渐形成', 'cognition_ids': ids}], 'details': []}
        else:
            content = {'body': '我们信任彼此能在分开后恢复连接，忙碌时仍需沟通。', 'change': '由事例形成当前认识',
                'understanding_indices': [0], 'cognition_ids': ['reorg-r']}
        result = {'choices': [{'message': {'content': json.dumps(content)}, 'finish_reason': 'stop'}]}
        raw = json.dumps(result).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def main():
    repo = Path(__file__).resolve().parents[2]
    assembly = repo / 'Tools/Host/bin/Release/net8.0/TraceSoul2.Host.dll'
    upstream = ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
    threading.Thread(target=upstream.serve_forever, daemon=True).start()
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def api(path, method='GET', payload=None, expected=200):
        request = urllib.request.Request(base + path, method=method,
            data=json.dumps(payload).encode() if payload is not None else None,
            headers={'Content-Type': 'application/json', 'Origin': base})
        try:
            with opener.open(request, timeout=30) as response:
                code, raw = response.status, response.read().decode()
        except urllib.error.HTTPError as error:
            code, raw = error.code, error.read().decode()
        assert code == expected, f'{method} {path}: {code}, expected {expected}'
        assert 'test-provider-key' not in raw and 'private-upstream-detail' not in raw
        return json.loads(raw) if raw else None

    try:
        with tempfile.TemporaryDirectory(prefix='tracesoul-identity-reorganization-') as folder:
            root = Path(folder)
            # Use a disposable fixture account; console encoding varies across Windows runners.
            password = 'Initial-fallback-fixture-2026!'
            salt = os.urandom(16)
            auth = dict(version=1, username='admin', algorithm='PBKDF2-SHA256', iterations=600000,
                salt=base64.b64encode(salt).decode(),
                passwordHash=base64.b64encode(hashlib.pbkdf2_hmac('sha256', password.encode(), salt, 600000)).decode(),
                sessionStamp=os.urandom(16).hex(), mustChangePassword=True, updatedAtUtc='2026-10-23T00:00:00Z')
            (root/'control-auth.json').write_text(json.dumps(auth), encoding='utf-8')
            (root/'soul').mkdir()
            (root/'plugins').mkdir()
            (root/'plugin-data').mkdir()
            with sqlite3.connect(root/'soul'/'tracesoul2-brainframe.sqlite3') as db:
                db.execute('PRAGMA user_version=0')
            db.close()
            env = {k: v for k, v in os.environ.items() if not k.startswith('TRACESOUL2_')}
            env.update(TRACESOUL2_HOME=str(root), TRACESOUL2_DATA=str(root/'soul'),
                TRACESOUL2_PLUGINS=str(root/'plugins'), TRACESOUL2_PLUGINS_DATA=str(root/'plugin-data'),
                TRACESOUL2_URLS=base, TRACESOUL2_PAUSE_AUTOMATIC_WORK='1')
            logfile = root/'host.log'
            with logfile.open('w', encoding='utf-8') as output:
                process = subprocess.Popen(['dotnet', str(assembly)], env=env, stdout=output, stderr=subprocess.STDOUT,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
                try:
                    deadline = time.monotonic() + 45
                    ready = False
                    while time.monotonic() < deadline:
                        try:
                            api('/auth/status')
                            ready = True
                            break
                        except OSError:
                            pass
                        assert process.poll() is None, 'Temporary Host exited: ' + logfile.read_text(encoding='utf-8', errors='replace')[-3000:].replace(password, '[fixture password]')
                        time.sleep(0.15)
                    assert ready, 'Temporary Host did not become ready'
                    api('/identity/reorganization/relation', expected=401)
                    api('/auth/login', 'POST', {'username': 'admin', 'password': password})
                    api('/auth/account', 'PUT', {'currentPassword': password, 'username': 'testadmin',
                        'newPassword': 'Temporary-Test-Password-123!', 'confirmPassword': 'Temporary-Test-Password-123!'})
                    api('/identity/pair', 'PUT', {'username': 'Test user', 'assname': 'Test soul'})
                    api('/providers/default', 'PUT', {'model': 'test-model', 'apiKey': 'test-provider-key',
                        'baseUrl': f'http://127.0.0.1:{upstream.server_port}/test', 'timeout': 5})
                    with sqlite3.connect(root/'soul'/'tracesoul2-brainframe.sqlite3') as db:
                        db.execute("INSERT INTO moments (Id,ConversationId,Role,Content,CreatedUnixMs) VALUES ('reorg-m','tracesoul2','user','Synthetic event',1)")
                        db.execute("INSERT INTO cognition_slices (Id,Status,IdentitySlot,Summary,ContextConversationId,MemoryVisibility) VALUES ('reorg-r','active','relation','Synthetic understanding','tracesoul2','private')")
                        db.execute("INSERT INTO cognition_evidence (Id,CognitionId,MomentId,Relation) VALUES ('reorg-e','reorg-r','reorg-m','support')")
                    db.close()
                    before = next(x for x in api('/identity/cards') if x['slot'] == 'relation')
                    api('/identity/reorganization/relation', 'POST', {'day': '', 'restart': False}, expected=202)
                    deadline = time.monotonic()+30
                    while time.monotonic()<deadline:
                        state = api('/identity/reorganization/relation')
                        if not state['busy']: break
                        time.sleep(.1)
                    draft = state['draft']
                    assert draft['status'] == 'ready', state
                    assert len(calls) == 2
                    assert next(x for x in api('/identity/cards') if x['slot'] == 'relation')['body'] == before['body']
                    assert draft['understandings'][0]['cognition_ids'] == ['reorg-r'] and draft['proposal']['change']
                    adopted = api('/identity/reorganization/relation/apply', 'POST', {'id': draft['id']})
                    assert adopted['status'] == 'applied'
                    after = next(x for x in api('/identity/cards') if x['slot'] == 'relation')
                    assert after['body'] == draft['proposal']['body'] and after['origin'] == 'derived' and after['current']
                    api('/identity/reorganization/relation/apply', 'POST', {'id': draft['id']})
                    assert len(calls) == 2
                    assert next(x for x in api('/identity/cards') if x['slot'] == 'relation')['revision'] == after['revision']
                    api('/identity/reorganization/relation', 'POST', {'day': '', 'restart': True}, expected=202)
                    deadline = time.monotonic()+30
                    while time.monotonic()<deadline:
                        state = api('/identity/reorganization/relation')
                        if not state['busy']: break
                        time.sleep(.1)
                    assert state['draft']['status'] == 'ready'
                    with sqlite3.connect(root/'soul'/'tracesoul2-brainframe.sqlite3') as db:
                        db.execute("UPDATE cognition_slices SET Summary='Revised after preview' WHERE Id='reorg-r'")
                    db.close()
                    api('/identity/reorganization/relation/apply', 'POST', {'id': state['draft']['id']}, expected=409)
                    assert len(calls) == 4
                    print('Temporary Host identity reorganization passed: authenticated preview, persisted sources, explicit apply, idempotency and stale rejection.')
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=10)
    finally:
        upstream.shutdown()
        upstream.server_close()


if __name__ == '__main__':
    main()
