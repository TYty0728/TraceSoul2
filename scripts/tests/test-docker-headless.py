"""Disposable Linux Docker image: real startup, bundled plugins, ONNX and no hardware."""
import base64
import hashlib
import http.cookiejar
import json
import os
import re
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid


def main():
    repo = Path(__file__).resolve().parents[2]
    name = 'tracesoul2-headless-' + uuid.uuid4().hex[:12]
    image = name + ':test'
    subprocess.run(['docker', 'build', '--build-arg', 'TARGETARCH=amd64', '-t', image, str(repo)], check=True)
    with tempfile.TemporaryDirectory(prefix='tracesoul-headless-') as folder:
        root = Path(folder)
        data = root / 'Data'
        data.mkdir()
        # A disposable account prevents generated credentials from entering CI logs.
        password = 'Docker-fixture-password-123!'
        salt = os.urandom(16)
        config = dict(Version=1, Username='docker-fixture', Algorithm='PBKDF2-SHA256', Iterations=600000,
                      Salt=base64.b64encode(salt).decode(),
                      PasswordHash=base64.b64encode(hashlib.pbkdf2_hmac('sha256', password.encode(), salt, 600000)).decode(),
                      SessionStamp=uuid.uuid4().hex, MustChangePassword=False, UpdatedAtUtc='2026-10-19T00:00:00Z')
        auth = data / 'control-auth.json'
        auth.write_text(json.dumps(config))
        auth.chmod(0o600)
        before = auth.read_bytes()
        args = ['docker', 'run', '-d', '--name', name, '--user', f'{os.getuid()}:{os.getgid()}',
                '-p', '127.0.0.1::5080', '--mount', f'type=bind,src={root},dst=/opt/tracesoul2',
                '-e', 'TRACESOUL2_URLS=http://0.0.0.0:5080',
                '-e', 'TRACESOUL2_TRUST_CONTAINER_PROXY=1', image]
        try:
            subprocess.run(args, check=True)
            port = subprocess.check_output(['docker', 'port', name, '5080/tcp'], text=True).strip().rsplit(':', 1)[1]
            base = 'http://127.0.0.1:' + port
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                                urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

            def api(path, method='GET', body=None):
                request = urllib.request.Request(base + path, method=method,
                    data=json.dumps(body).encode() if body is not None else None,
                    headers={'Content-Type': 'application/json', 'Origin': base})
                with opener.open(request, timeout=30) as response:
                    return json.load(response)

            def ready():
                deadline = time.monotonic() + 60
                last_problem = 'no response'
                while time.monotonic() < deadline:
                    try:
                        api('/auth/status')
                        return
                    except (OSError, ValueError) as error:
                        last_problem = str(error)
                        time.sleep(0.3)
                raise AssertionError('Headless Docker Host did not become ready: ' + last_problem)

            ready()
            api('/auth/login', 'POST', dict(username='docker-fixture', password=password))
            status = api('/wxxz')
            assert status['sessionId'] is None and status['monitor']['active'] is False
            assert status['displayState']['version'] == 1 and status['displayState']['state'] == 'offline'
            catalog = api('/expressions')
            assert len(catalog['tags']) == 42 and len(catalog['semanticTags']) >= 31
            match = api('/expressions/preview', 'POST', dict(text='这是一条公开的容器本地语义检查。', mood='', topTags=4))
            assert match['reason'] != 'embedding_unavailable', 'Container must load its bundled CPU ONNX model'
            plugins = api('/plugins')
            sticker = next(p for p in plugins if p['id'] == 'qq.sticker')
            assert not sticker.get('loadError'), 'Bundled QQ sticker contract must match the Host'
            manifest = json.loads((root / 'App/tracesoul2.install.json').read_text(encoding='utf-8-sig'))
            assert len(manifest['bundledPlugins']) == 9
            for plugin in manifest['bundledPlugins']:
                assert (root / 'Plugins' / plugin / 'plugin.json').is_file()
            keep = root / 'plugins_data/local-voice/keep.txt'
            keep.parent.mkdir(exist_ok=True)
            keep.write_text('private runtime stays')
            marker = root / 'Plugins/qq-sticker/keep.txt'
            marker.write_text('installed plugin stays')
            subprocess.run(['docker', 'restart', name], check=True)
            # Docker may allocate a different host port when an ephemeral
            # publication is restarted; always observe the current mapping.
            port = subprocess.check_output(['docker', 'port', name, '5080/tcp'], text=True).strip().rsplit(':', 1)[1]
            base = 'http://127.0.0.1:' + port
            ready()
            assert auth.read_bytes() == before
            assert keep.read_text() == 'private runtime stays' and marker.read_text() == 'installed plugin stays'
            print('Source Docker passed: non-root startup, 9 official plugins, private auth, CPU ONNX, offline WXXZ and persistent restart.')
        except Exception:
            logs = subprocess.run(['docker', 'logs', '--tail', '60', name], capture_output=True, text=True)
            diagnostic = logs.stdout + logs.stderr
            diagnostic = re.sub(r'(初始密码[：:])[^\r\n]*', r'\1 [redacted]', diagnostic)
            print(diagnostic[-12000:])
            raise
        finally:
            subprocess.run(['docker', 'rm', '-f', name], check=False)
            subprocess.run(['docker', 'image', 'rm', image], check=False)


if __name__ == '__main__':
    main()
