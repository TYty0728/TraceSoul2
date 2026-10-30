"""Supervise the local bridge and reconnect its SSH tunnel from private config."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', type=Path, required=True)
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding='utf-8-sig'))
    state = Path(config['state_dir'])
    state.mkdir(parents=True, exist_ok=True)
    stop = state / 'stop'
    # A second launch (for example at login) must not create a second tunnel.
    with (state / 'service.lock').open('a+b') as lease:
        if os.name == 'nt':
            import msvcrt
            lease.write(b'0'); lease.flush(); lease.seek(0)
            msvcrt.locking(lease.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
        env = dict(os.environ, TRACESOUL_CODEX_BRIDGE_TOKEN=config['token'])
        if config.get('python_path'):
            env['PYTHONPATH'] = config['python_path']
        folder = Path(__file__).resolve().parent
        bridge_command = [sys.executable, '-u', str(folder / 'bridge.py'),
                          '--port', str(config['port']), '--model', config['model'],
                          '--state-dir', str(state), '--codex', config['codex'],
                          '--concurrency', str(config.get('concurrency', 1))]
        tunnel_command = [sys.executable, '-u', str(folder / 'tunnel.py'),
                          '--connection-config', config['ssh_config'],
                          '--local-port', str(config['port']), '--remote-port', str(config['port']),
                          '--state-file', str(state / 'tunnel-state.json'), '--stop-file', str(stop)]
        processes = {}
        try:
            while not stop.exists():
                for name, command in [('bridge', bridge_command), ('tunnel', tunnel_command)]:
                    process = processes.get(name)
                    if process is None or process.poll() is not None:
                        log = state / (name + '.log')
                        if log.exists() and log.stat().st_size > 2_000_000:
                            log.replace(state / (name + '.previous.log'))
                        with log.open('ab') as output:
                            processes[name] = subprocess.Popen(command, env=env, cwd=state,
                                stdin=subprocess.DEVNULL, stdout=output, stderr=output,
                                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
                status = {'pid': os.getpid(), 'updated_unix': time.time(),
                          'children': {name: {'pid': p.pid, 'running': p.poll() is None}
                                       for name, p in processes.items()}}
                temporary = state / 'service-state.tmp'
                temporary.write_text(json.dumps(status), encoding='utf-8')
                temporary.replace(state / 'service-state.json')
                time.sleep(10)
        finally:
            # Let the tunnel release its namespace listener before terminating it.
            stop.touch()
            tunnel = processes.get('tunnel')
            if tunnel and tunnel.poll() is None:
                try:
                    tunnel.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    tunnel.terminate()
            for process in processes.values():
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=10)


if __name__ == '__main__':
    main()
