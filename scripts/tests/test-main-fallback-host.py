"""Real isolated Host, mock inference only; production souls and providers are never opened."""
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import socket
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
        success = self.path.startswith('/tertiary/')
        result = {'choices': [{'message': {'content': json.dumps({'step': 'finish', 'reply': 'offline fallback ok'})},
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
                    password = None
                    while time.monotonic() < deadline:
                        raw = logfile.read_bytes()
                        match = next((m for encoding in ('utf-8', 'gb18030')
                            if (m := re.search(r'初始密码：([^\r\n]+)', raw.decode(encoding, errors='replace')))), None)
                        if match:
                            password = match.group(1).strip()
                            try:
                                api('/auth/status')
                                break
                            except OSError:
                                pass
                        assert process.poll() is None, 'Temporary Host exited'
                        time.sleep(0.15)
                    assert password
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
                    assert not calls, 'Saving choices must not call a model'
                    api('/identity/pair', 'PUT', {'username': 'Test user', 'assname': 'Test soul'})
                    result = api('/moments', 'POST', {'content': 'Isolated fallback check'})
                    assert result['reply'] == 'offline fallback ok'
                    assert [p for p, _ in calls] == ['/default/chat/completions', '/secondary/chat/completions', '/tertiary/chat/completions']
                    assert all(body['messages'] == calls[0][1]['messages'] for _, body in calls)
                    assert api('/providers/slots')['chat']['providerId'] == 'default'
                    assert not api('/runtime/failures'), 'Recovered inference must not send a failure notification'
                    api('/providers/main-fallbacks', 'PUT', {'secondary': {}, 'tertiary': {}})
                    assert not api('/providers/main-fallbacks')['secondary']['providerId']
                    assert len(calls) == 3
                    print('Temporary Host fallback checks passed: auth, atomic settings, no save-time calls, three-stage inference and original context.')
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
