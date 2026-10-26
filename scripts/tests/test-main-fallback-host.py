"""Real isolated Host, mock inference only; production souls and providers are never opened."""
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
maintenance_calls = []
maintenance_received = threading.Event()


class Upstream(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        # The private turn now also schedules an independent, bounded state update.
        # Keep it observable without counting it as another dialogue fallback attempt.
        maintenance = any(m.get('role') == 'system' and
            m.get('content', '').startswith('整理本轮之后仍然相关的关注和共同文字场景。')
            for m in body.get('messages', []))
        if maintenance:
            maintenance_calls.append((self.path, body))
            maintenance_received.set()
        else:
            calls.append((self.path, body))
        success = maintenance or self.path.startswith('/tertiary/')
        content = {'attention': [], 'scene': None} if maintenance else {'step': 'finish', 'reply': 'offline fallback ok'}
        result = {'choices': [{'message': {'content': json.dumps(content)},
                               'finish_reason': 'stop'}]} if success else {'error': {'message': 'private-upstream-detail'}}
        raw = json.dumps(result).encode()
        self.send_response(200 if success else 503)
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
        with tempfile.TemporaryDirectory(prefix='tracesoul-fallback-host-') as folder:
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
                    api('/providers/main-fallbacks', expected=401)
                    api('/auth/login', 'POST', {'username': 'admin', 'password': password})
                    api('/auth/account', 'PUT', {'currentPassword': password, 'username': 'testadmin',
                        'newPassword': 'Temporary-Test-Password-123!', 'confirmPassword': 'Temporary-Test-Password-123!'})
                    old = api('/providers/main-fallbacks')
                    assert not old['secondary']['providerId'] and not old['tertiary']['providerId']
                    for name in ('default', 'secondary', 'tertiary'):
                        api('/providers/' + name, 'PUT', {'model': name + '-model', 'apiKey': 'test-provider-key',
                            'baseUrl': f'http://127.0.0.1:{upstream.server_port}/{name}', 'timeout': 5, 'transientRetries': 2})
                    api('/providers/current', 'PUT', {'id': 'default', 'model': 'default-model'})
                    choices = {name: {'providerId': name, 'model': name + '-model'} for name in ('secondary', 'tertiary')}
                    assert api('/providers/main-fallbacks', 'PUT', choices) == choices
                    assert api('/providers/main-fallbacks') == choices
                    api('/providers/main-fallbacks', 'PUT', {'secondary': choices['tertiary'], 'tertiary': choices['tertiary']}, expected=400)
                    assert api('/providers/main-fallbacks') == choices
                    assert not calls and not maintenance_calls, 'Saving choices must not call a model'
                    api('/identity/pair', 'PUT', {'username': 'Test user', 'assname': 'Test soul'})
                    result = api('/moments', 'POST', {'content': 'Isolated fallback check'})
                    assert result['reply'] == 'offline fallback ok'
                    assert [p for p, _ in calls] == ['/default/chat/completions', '/secondary/chat/completions', '/tertiary/chat/completions']
                    assert all(body['messages'] == calls[0][1]['messages'] for _, body in calls)
                    assert maintenance_received.wait(5), 'Private turn must schedule its independent state update'
                    assert len(maintenance_calls) == 1, 'State update must not repeat the dialogue fallback chain'
                    assert maintenance_calls[0][1]['messages'] != calls[0][1]['messages']
                    assert api('/providers/slots')['chat']['providerId'] == 'default'
                    assert not api('/runtime/failures'), 'Recovered inference must not send a failure notification'
                    api('/providers/main-fallbacks', 'PUT', {'secondary': {}, 'tertiary': {}})
                    assert not api('/providers/main-fallbacks')['secondary']['providerId']
                    assert len(calls) == 3
                    assert len(maintenance_calls) == 1
                    print('Temporary Host fallback checks passed: auth, atomic settings, no save-time calls, three-stage inference, original context and independent state update.')
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
