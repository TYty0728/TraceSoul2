"""Authenticated Codex health check over the shared private SSH transport."""
import json
import os
from pathlib import Path
import sys
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'SensoryBoard'))
import remote_tunnel


def check_codex(port):
    token = os.environ['TRACESOUL_CODEX_BRIDGE_TOKEN']
    request = urllib.request.Request(f'http://127.0.0.1:{port}/v1/models',
                                     headers={'Authorization': 'Bearer ' + token})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(request, timeout=5) as response:
        result = json.load(response)
    if result.get('object') != 'list' or not result.get('data'):
        raise ValueError('Local Codex model catalog is unavailable')


if __name__ == '__main__':
    remote_tunnel.check_local_bridge = check_codex
    try:
        remote_tunnel.main()
    except KeyboardInterrupt:
        pass
    except Exception as error:
        print('Codex tunnel stopped: ' + type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
