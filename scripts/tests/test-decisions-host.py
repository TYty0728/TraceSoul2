"""Exercise native Decisions through a real temporary Host and loopback upstream; no real keys."""
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request


requests = []
proxy_connects = []


class Upstream(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_CONNECT(self):
        proxy_connects.append(self.path)
        self.send_error(502)

    def do_GET(self):
        self.reply()

    def do_POST(self):
        self.reply()

    def reply(self):
        raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        requests.append((self.path, dict(self.headers), json.loads(raw) if raw else None))
        status = 200
        if 'models' in self.path:
            result = {'data': [{'id': 'typesafe/jev-1.14'}, {'id': 'openai/gpt-6-luna-decisions'}]}
        elif self.path.endswith('/decisions'):
            if self.path.startswith('/denied'):
                status, result = 403, {'error': {'message': 'test-upstream-secret test-relay-secret'}}
            elif self.path.startswith('/redirect'):
                self.send_response(307)
                self.send_header('Location', '/must-not-follow')
                self.end_headers()
                return
            else:
                result = {'answers': {'next_step': {'type': 'choice', 'choice': 'wait', 'confidence': 0.8,
                           'probabilities': {'wait': 0.9, 'engage': 0.1}}}, 'usage': {'cost': 0.00001}}
        else:
            status, result = 400, {'error': 'unexpected route'}
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps(result).encode())


def run():
    repo = Path(__file__).resolve().parents[2]
    assembly = repo / 'Tools/Host/bin/Debug/net8.0/TraceSoul2.Host.dll'
    upstream = ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
    threading.Thread(target=upstream.serve_forever, daemon=True).start()
    mock_url = f'http://127.0.0.1:{upstream.server_port}'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
              urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def api(path, method='GET', payload=None, expect=200):
        req = urllib.request.Request(base + path, method=method,
              data=json.dumps(payload).encode() if payload is not None else None,
              headers={'Content-Type': 'application/json', 'Origin': base})
        try:
            with opener.open(req, timeout=15) as response:
                code, raw = response.status, response.read().decode()
        except urllib.error.HTTPError as error:
            code, raw = error.code, error.read().decode()
        assert code == expect, f'{method} {path}: status {code}, expected {expect}'
        assert 'test-upstream-secret' not in raw and 'test-relay-secret' not in raw, 'credential leaked'
        return json.loads(raw)

    try:
        with tempfile.TemporaryDirectory(prefix='tracesoul-decisions-host-') as root:
            env = {k: v for k, v in os.environ.items() if not k.startswith('TRACESOUL2_')}
            env.update(TRACESOUL2_HOME=root, TRACESOUL2_DATA=str(Path(root)/'soul'),
                       TRACESOUL2_PLUGINS=str(Path(root)/'plugins'), TRACESOUL2_PLUGINS_DATA=str(Path(root)/'plugin-data'),
                       TRACESOUL2_URLS=base)
            log_path = Path(root)/'host.log'
            with log_path.open('w', encoding='utf-8') as output:
                process = subprocess.Popen(['dotnet', str(assembly)], env=env, stdout=output, stderr=subprocess.STDOUT,
                          creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
                try:
                    deadline = time.monotonic() + 45
                    password = None
                    while time.monotonic() < deadline:
                        try:
                            api('/auth/status')
                        except (OSError, AssertionError):
                            pass
                        raw_log = log_path.read_bytes()
                        match = next((m for encoding in ('utf-8', 'gb18030')
                                      if (m := re.search(r'初始密码：([^\r\n]+)', raw_log.decode(encoding, errors='replace')))), None)
                        if match:
                            password = match.group(1).strip()
                            try:
                                api('/auth/status')
                                break
                            except (OSError, AssertionError):
                                pass
                        if process.poll() is not None:
                            raise AssertionError('Temporary Host exited before readiness')
                        time.sleep(0.15)
                    assert password, 'Temporary Host did not initialize'
                    api('/auth/login', 'POST', {'username': 'admin', 'password': password})
                    api('/auth/account', 'PUT', {'currentPassword': password, 'username': 'testadmin',
                        'newPassword': 'Temporary-Test-Password-123!', 'confirmPassword': 'Temporary-Test-Password-123!'})
                    templates = api('/providers/templates')
                    assert any(t['type'] == 'openrouter_decisions' for t in templates)
                    provider = api('/providers', 'POST', {'templateKey': 'openrouter-decisions'})
                    pid = provider['id']
                    config = {'type': 'openrouter_decisions', 'model': 'typesafe/jev-1.14', 'decisionMode': 'direct',
                              'baseUrl': mock_url + '/api', 'apiKey': 'test-upstream-secret',
                              'relayKey': 'test-relay-secret', 'timeout': 10}
                    api('/providers/' + pid, 'PUT', config)
                    assert not requests, 'Saving configuration must not call upstream'
                    api('/providers/' + pid + '/models', 'POST')
                    assert requests[-1][0] == '/api/v1/models?output_modalities=decisions'
                    assert 'X-TraceSoul-Relay-Key' not in requests[-1][1]
                    assert requests[-1][2] is None
                    api('/providers/slots', 'PUT', {'jev': {'providerId': pid, 'model': 'typesafe/jev-1.14'}})
                    assert api('/providers/slots')['jev']['model'] == 'typesafe/jev-1.14'
                    result = api('/providers/' + pid + '/decision-test', 'POST', {'model': 'typesafe/jev-1.14'})
                    assert result['choice'] == 'wait' and requests[-1][0] == '/api/alpha/decisions'
                    assert 'messages' not in requests[-1][2] and requests[-1][1]['Authorization'] == 'Bearer test-upstream-secret'
                    api('/providers/current', 'PUT', {'id': pid, 'model': 'typesafe/jev-1.14'}, expect=400)
                    api('/providers/slots', 'PUT', {'review': {'providerId': pid, 'model': 'typesafe/jev-1.14'}}, expect=400)
                    config.update(decisionMode='relay', baseUrl=mock_url + '/relay', apiKey='', relayKey='')
                    public = api('/providers/' + pid, 'PUT', config)
                    assert public['hasApiKey'] and public['hasRelayKey'] and 'relayKey' not in public
                    api('/providers/' + pid + '/decision-test', 'POST', {'model': 'typesafe/jev-1.14'})
                    assert requests[-1][0] == '/relay/decisions' and requests[-1][1]['X-TraceSoul-Relay-Key'] == 'test-relay-secret'
                    for prefix in ('denied', 'redirect'):
                        config['baseUrl'] = mock_url + '/' + prefix
                        api('/providers/' + pid, 'PUT', config)
                        before = len(requests)
                        api('/providers/' + pid + '/decision-test', 'POST', {'model': 'typesafe/jev-1.14'}, expect=400)
                        assert len(requests) == before + 1, 'must not retry or follow redirect'
                    config.update(decisionMode='direct', baseUrl='https://openrouter.ai/api', proxy=mock_url)
                    api('/providers/' + pid, 'PUT', config)
                    api('/providers/' + pid + '/models', 'POST', expect=400)
                    assert proxy_connects == ['openrouter.ai:443'], 'explicit Host proxy must be used'
                    config.update(clearRelayKey=True, proxy='')
                    public = api('/providers/' + pid, 'PUT', config)
                    assert not public['hasRelayKey']
                    print('Temporary Host checks passed: provider API, native paths, JEV slots, keys, errors, redirects and explicit proxy; no external inference.')
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
    run()
