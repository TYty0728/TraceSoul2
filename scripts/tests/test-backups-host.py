"""Real temporary Host: authenticated archives and offline recovery; no models or real data."""
import http.cookiejar
import hashlib
import base64
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


def run():
    repo = Path(__file__).resolve().parents[2]
    assembly = Path(sys.argv[1]).resolve() if len(sys.argv)>1 else repo / 'Tools/Host/bin/Debug/net8.0/TraceSoul2.Host.dll'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def api(path, method='GET', payload=None, expect=200, binary=False, origin=None):
        request = urllib.request.Request(base + path, method=method,
            data=json.dumps(payload).encode() if payload is not None else None,
            headers={'Content-Type':'application/json', 'Origin':origin or base})
        try:
            with opener.open(request, timeout=40) as response:
                code, raw, headers = response.status, response.read(), response.headers
        except urllib.error.HTTPError as error:
            code, raw, headers = error.code, error.read(), error.headers
        assert code == expect, f'{method} {path}: {code}, expected {expect}'
        if binary:
            assert headers.get('Cache-Control') == 'no-store', 'private downloads must not be cached'
            return raw
        return json.loads(raw)

    with tempfile.TemporaryDirectory(prefix='tracesoul-backups-host-') as folder:
        root = Path(folder)
        application = root/'App'
        shutil.copytree(assembly.parent, application)
        assembly = application/assembly.name
        misplaced = application/'plugin-data'
        misplaced.mkdir()
        (misplaced/'config.json').write_text(json.dumps({'api_key':'synthetic-preserved',
            'reference_path':str(misplaced/'reference.wav')}),encoding='utf-8')
        original_hash=hashlib.sha256((misplaced/'config.json').read_bytes()).hexdigest()
        env = {k:v for k,v in os.environ.items() if not k.startswith('TRACESOUL2_')}
        (root/'home').mkdir()
        # A disposable pre-seeded account avoids localized console parsing and printing generated credentials.
        password='Initial-archive-fixture-2026!'
        salt=os.urandom(16)
        auth=dict(version=1,username='admin',algorithm='PBKDF2-SHA256',iterations=600000,
            salt=base64.b64encode(salt).decode(),
            passwordHash=base64.b64encode(hashlib.pbkdf2_hmac('sha256',password.encode(),salt,600000)).decode(),
            sessionStamp=os.urandom(16).hex(),mustChangePassword=True,updatedAtUtc='2026-10-20T00:00:00Z')
        (root/'home'/'control-auth.json').write_text(json.dumps(auth),encoding='utf-8')
        (root/'home'/'soul').mkdir()
        (root/'plugins').mkdir()
        with sqlite3.connect(root/'home'/'soul'/'tracesoul2-brainframe.sqlite3') as db:
            db.execute('PRAGMA user_version=0')
        db.close()
        env.update(TRACESOUL2_HOME=str(root/'home'), TRACESOUL2_DATA=str(root/'home'/'soul'),
            TRACESOUL2_PLUGINS=str(root/'plugins'), TRACESOUL2_PLUGINS_DATA=str(misplaced), TRACESOUL2_URLS=base)
        logfile = root/'host.log'
        with logfile.open('w', encoding='utf-8') as output:
            process = subprocess.Popen(['dotnet',str(assembly)],env=env,stdout=output,stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            try:
                ready = False
                deadline = time.monotonic()+45
                while time.monotonic() < deadline:
                    try:
                        api('/auth/status')
                        ready = True
                        break
                    except (OSError,AssertionError):
                        pass
                    if process.poll() is not None:
                        raise AssertionError('temporary Host exited: '+logfile.read_text(encoding='utf-8',errors='replace')[-4000:])
                    time.sleep(.15)
                assert ready, 'temporary Host did not become ready'
                api('/backups',expect=401)
                api('/backups/recovery-key',expect=401)
                api('/auth/login','POST',{'username':'admin','password':password})
                api('/auth/account','PUT',{'currentPassword':password,'username':'testadmin',
                    'newPassword':'Archive-check-only-2026!','confirmPassword':'Archive-check-only-2026!'})
                state=api('/backups')
                assert state['settings']['enabled'] is False and state['packages']==[]
                assert len(state['pathRepairs'])==1 and misplaced.is_dir(), 'startup must retain original path'
                repaired=Path(state['sources']['pluginsData'])
                config=json.loads((repaired/'config.json').read_text(encoding='utf-8'))
                assert config['api_key']=='synthetic-preserved' and config['reference_path']==str(repaired/'reference.wav')
                assert Path(state['pathRepairs'][0]['originalBackup']).samefile(misplaced), 'original directory identity changed'
                assert Path(state['pathRepairs'][0]['source']).samefile(misplaced), 'source directory identity changed'
                assert state['pathRepairs'][0]['copyVerifiedUtc'] and len(state['pathRepairs'][0]['sourceFingerprint'])==64
                assert hashlib.sha256((misplaced/'config.json').read_bytes()).hexdigest()==original_hash, 'original configuration changed'
                assert state['settings']['keepCount']==1 and state['settings']['intervalHours']==168 and not state['protection']['active']
                api('/backups/settings','PUT',{'enabled':True,'directory':'archives','intervalHours':168,'keepCount':2,'extras':{}},expect=400)
                api('/backups/settings','PUT',{'enabled':True,'directory':'archives','intervalHours':168,'keepCount':1,'extras':{}})
                api('/backups/create','POST',origin='http://invalid.example',expect=403)
                assert api('/backups/create','POST')['started']
                deadline=time.monotonic()+45
                while time.monotonic()<deadline:
                    state=api('/backups')
                    if not state['busy']:
                        break
                    time.sleep(.1)
                assert state['phase']=='completed', state.get('error')
                assert len(state['packages'])==1
                item=state['packages'][0]
                assert api('/backups/'+item['id']+'/verify','POST')['valid']
                package=root/'saved.tsbackup'
                package.write_bytes(api('/backups/'+item['id']+'/download',binary=True))
                keyfile=root/'saved.key'
                keyfile.write_bytes(api('/backups/recovery-key',binary=True))
                assert package.read_bytes()[:8]==b'TSBAK001'
                offline=root/'offline-home-must-not-be-created'
                offline_env={**env,'TRACESOUL2_HOME':str(offline)}
                destination=root/'restored'
                result=subprocess.run(['dotnet',str(assembly),'--archive-extract',str(package),str(keyfile),str(destination)],
                    env=offline_env,capture_output=True,timeout=40)
                assert result.returncode==0, 'offline extraction failed'
                assert not offline.exists(), 'offline CLI initialized live home'
                manifest=json.loads((destination/'manifest.json').read_text(encoding='utf-8'))
                assert (destination/'Data'/'souls'/manifest['activeSoul']/'tracesoul2-brainframe.sqlite3').is_file()
                assert not list(destination.rglob('archive-recovery.key'))
                assert not list(destination.rglob('review-execution.lock')), 'process-owned execution locks must not be archived'
                home=json.loads((destination/'Data'/'home.json').read_text(encoding='utf-8'))
                assert home['pluginsDirectory']=='../Plugins'
                again=subprocess.run(['dotnet',str(assembly),'--archive-extract',str(package),str(keyfile),str(destination)],
                    env=offline_env,capture_output=True,timeout=40)
                assert again.returncode!=0, 'offline recovery overwrote an existing target'
                assert hashlib.sha256((misplaced/'config.json').read_bytes()).hexdigest()==original_hash, 'original changed after full backup/recovery cycle'
                # Simulate a version change and overdue schedule across a real Host restart.
                process.terminate();process.wait(timeout=15)
                settings_path=root/'home'/'archive-settings.json'
                plan=json.loads(settings_path.read_text(encoding='utf-8'))
                plan['acceptedHostVersion']='0.0.0'
                plan['lastAttemptUtc']='2000-01-01T00:00:00Z'
                settings_path.write_text(json.dumps(plan),encoding='utf-8')
                retained=root/'home'/'archives'/'packages'/(item['id']+'.tsbackup')
                retained_hash=hashlib.sha256(retained.read_bytes()).hexdigest()
                process=subprocess.Popen(['dotnet',str(assembly)],env=env,stdout=output,stderr=subprocess.STDOUT,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
                deadline=time.monotonic()+45
                while time.monotonic()<deadline:
                    try:
                        api('/auth/status');break
                    except (OSError,AssertionError):time.sleep(.15)
                assert process.poll() is None,'restarted Host exited'
                api('/auth/login','POST',{'username':'testadmin','password':'Archive-check-only-2026!'})
                protected=api('/backups')
                assert protected['protection']['active'] and protected['nextUtc'] is None
                assert len(protected['packages'])==1 and protected['packages'][0]['id']==item['id']
                api('/backups/create','POST',expect=400)
                api('/backups/confirm-version','POST',{'version':'0.0.0','packageId':item['id']},expect=400)
                assert hashlib.sha256(retained.read_bytes()).hexdigest()==retained_hash
                released=api('/backups/confirm-version','POST',{'version':protected['protection']['currentVersion'],'packageId':item['id']})
                assert not released['protection']['active'] and released['nextUtc'] and retained.exists()
                assert api('/backups/create','POST')['started']
                deadline=time.monotonic()+45
                while time.monotonic()<deadline:
                    state=api('/backups')
                    if not state['busy']:break
                    time.sleep(.1)
                assert state['phase']=='completed' and len(state['packages'])==1 and not retained.exists()
            finally:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
    print('Real temporary Host backup checks passed: single verified archive, restart/version protection, explicit confirmation, retained originals, auth/origin and offline recovery.')


if __name__ == '__main__':
    if os.name=='nt':
        # Windows runners use 8.3 aliases for TEMP; .NET may expand them to full names.
        # Exercise that spelling difference locally as well, comparing directory identity.
        import ctypes
        with tempfile.TemporaryDirectory(prefix='tracesoul-windows-paths-') as parent:
            buffer=ctypes.create_unicode_buffer(32768)
            length=ctypes.windll.kernel32.GetShortPathNameW(parent,buffer,len(buffer))
            previous=tempfile.tempdir
            tempfile.tempdir=buffer.value if length else parent
            try:
                run()
            finally:
                tempfile.tempdir=previous
    else:
        run()
