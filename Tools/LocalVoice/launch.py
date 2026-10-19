"""Start an installed loopback voice service without requiring PowerShell script execution."""
import argparse
import json
from pathlib import Path
import socket
import subprocess
import time
import urllib.request
from access import load_key,private_opener


def health(url,key):
    try:
        with private_opener().open(urllib.request.Request(url+'/health',headers={'Authorization':'Bearer '+key}),timeout=2) as response:
            data=json.load(response)
        return data.get('model')=='Qwen3-TTS-12Hz-0.6B-Base' and data.get('ready') is True and data.get('authentication_required') is True
    except (OSError,ValueError):
        return False


def ensure_ready(root,url,key):
    python=root/'venv'/'Scripts'/'python.exe'
    server=root/'app'/'server.py'
    if not python.is_file() or not server.is_file():
        raise RuntimeError('Local Qwen TTS is not installed in the configured directory.')
    process=None
    if not health(url,key):
        with socket.socket() as probe:
            occupied=probe.connect_ex(('127.0.0.1',5088))==0
        if occupied:
            # An unrelated or unready listener is never stopped or replaced.
            raise RuntimeError('Port 5088 is occupied or the model is still loading. Wait and try again.')
        with (root/'stdout.log').open('wb') as output,(root/'stderr.log').open('wb') as error:
            process=subprocess.Popen([str(python),'-u',str(server),'--runtime',str(root),'--port','5088'],
                cwd=root,stdout=output,stderr=error,stdin=subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW)
        print('Starting local Qwen TTS. Loading the model...',flush=True)
        deadline=time.monotonic()+60
        while not health(url,key):
            if process.poll() is not None:
                raise RuntimeError('TTS exited during startup. See stderr.log in the runtime directory.')
            if time.monotonic()>=deadline:
                raise RuntimeError('Model is not ready yet. See stderr.log, or wait and try again.')
            time.sleep(1)
    print('Local Qwen TTS is ready: '+url+'/',flush=True)


def main():
    import msvcrt
    parser=argparse.ArgumentParser()
    parser.add_argument('--runtime',type=Path,required=True)
    parser.add_argument('--open',action='store_true')
    args=parser.parse_args()
    root=args.runtime.resolve(strict=True)
    url='http://127.0.0.1:5088'
    # Keep repeated double-clicks from loading duplicate GPU models before the port opens.
    with (root/'launch.lock').open('a+b') as lock:
        if lock.tell()==0:
            lock.write(b'0');lock.flush()
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
        except OSError:
            raise RuntimeError('Local TTS is already starting. Wait for its startup window to finish.') from None
        key=load_key(root)
        ensure_ready(root,url,key)
    if args.open:
        import webbrowser
        request=urllib.request.Request(url+'/auth/ticket',data=b'',headers={'Authorization':'Bearer '+key})
        with private_opener().open(request,timeout=5) as response:
            ticket=json.load(response)['ticket']
        webbrowser.open(url+'/auth/launch?ticket='+ticket)


if __name__=='__main__':
    try:
        main()
    except Exception as error:
        print(str(error),flush=True)
        raise SystemExit(1)
