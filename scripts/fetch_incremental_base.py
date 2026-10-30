"""Best-effort public inventory fetch; missing inventories keep full releases working."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import urllib.request
from build_incremental import validate


def fetch(repository, output):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Invalid repository')
    def get(url):
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'TraceSoul2-release'}), timeout=15) as response:
            value = response.read(8 * 1024 * 1024 + 1)
            if len(value) > 8 * 1024 * 1024:
                raise ValueError('Inventory response is too large')
            return value
    output = Path(output); output.mkdir(parents=True, exist_ok=True)
    for runtime in ('win-x64', 'linux-x64', 'linux-arm64'):
        (output / (runtime + '.json')).unlink(missing_ok=True)
    try:
        release = json.loads(get('https://api.github.com/repos/' + repository + '/releases/latest'))
        tag = release['tag_name']
        if not re.fullmatch(r'v\d+\.\d+\.\d+', tag):
            raise ValueError('Invalid base tag')
        names = {a['name'] for a in release['assets']}
    except Exception as error:
        print('Base inventory unavailable (' + type(error).__name__ + '); publishing full packages.')
        return
    for runtime in ('win-x64', 'linux-x64', 'linux-arm64'):
        name = f'tracesoul2-{runtime}-{tag}.files.json'
        target = output / (runtime + '.json')
        if name not in names or name + '.sha256' not in names:
            continue
        try:
            url = f'https://github.com/{repository}/releases/download/{tag}/{name}'
            raw = get(url); expected = get(url + '.sha256').decode('utf-8-sig').split()[0]
            if hashlib.sha256(raw).hexdigest() != expected:
                raise ValueError('Inventory checksum mismatch')
            manifest = json.loads(raw)
            validate(manifest, runtime)
            if manifest['version'] != tag[1:]: raise ValueError('Inventory version mismatch')
            target.write_bytes(raw)
        except Exception as error:
            print(runtime + ': base inventory unavailable (' + type(error).__name__ + '); full package only.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--repository', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args(); fetch(args.repository, args.output)
