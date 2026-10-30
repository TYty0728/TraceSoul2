"""Publish a complete file inventory and an optional previous-version delta ZIP."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import zipfile

INVENTORY = 'tracesoul2.files.json'
DELTA = 'tracesoul2.delta.json'


def digest(path):
    with path.open('rb') as stream:
        value = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b''): value.update(block)
        return value.hexdigest()


def safe_path(value):
    return (isinstance(value, str) and 0 < len(value) <= 512 and
            not any(ord(c) < 32 or c in '\\:*?"<>|' for c in value) and
            all(part and part not in ('.', '..') and part[-1] not in '. ' and
                not re.match(r'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)', part, re.I)
                for part in value.split('/')) and not value.startswith('/'))


def validate(manifest, runtime):
    if (manifest.get('schema') != 1 or manifest.get('product') != 'TraceSoul2' or
            manifest.get('runtime') != runtime or
            not re.fullmatch(r'\d+\.\d+\.\d+', manifest.get('version', ''))):
        raise ValueError('Invalid file inventory identity')
    seen = set()
    files = manifest.get('files')
    if not isinstance(files, list) or not 1 <= len(files) <= 20000:
        raise ValueError('Invalid file inventory size')
    for file in files:
        name = file.get('path', '')
        if (not safe_path(name) or name.lower() in (INVENTORY, DELTA) or name.lower() in seen or
                type(file.get('size')) is not int or not 0 <= file['size'] <= 2 * 1024**3 or
                not re.fullmatch('[a-f0-9]{64}', file.get('sha256', ''))):
            raise ValueError('Invalid file inventory entry')
        seen.add(name.lower())
    if sum(f['size'] for f in files) > 4 * 1024**3:
        raise ValueError('File inventory exceeds expanded package limit')


def write_hash(path):
    path.with_name(path.name + '.sha256').write_text(digest(path) + '  ' + path.name + '\n', encoding='ascii')


def build(package, output, runtime, version, base=None):
    package, output = Path(package), Path(output)
    output.mkdir(parents=True, exist_ok=True)
    files = []
    for path in sorted(package.rglob('*')):
        if path.is_symlink():
            raise ValueError('Package symlinks are not supported')
        if not path.is_file() or path.relative_to(package).as_posix() in (INVENTORY, DELTA):
            continue
        files.append({'path': path.relative_to(package).as_posix(), 'size': path.stat().st_size, 'sha256': digest(path)})
    manifest = dict(schema=1, product='TraceSoul2', version=version, runtime=runtime, files=files)
    validate(manifest, runtime)
    raw = json.dumps(manifest, ensure_ascii=False, indent=2).encode('utf-8')
    (package / INVENTORY).write_bytes(raw)
    asset = output / f'tracesoul2-{runtime}-v{version}.files.json'
    asset.write_bytes(raw); write_hash(asset)
    if not base or not Path(base).is_file():
        return None
    previous = json.loads(Path(base).read_text(encoding='utf-8-sig'))
    validate(previous, runtime)
    if tuple(map(int, previous['version'].split('.'))) >= tuple(map(int, version.split('.'))):
        return None
    old = {f['path']: f for f in previous['files']}
    changed = [f for f in files if old.get(f['path']) != f]
    # A delta with no reusable bytes adds no value; the full ZIP remains available.
    if sum(f['size'] for f in changed) >= sum(f['size'] for f in files):
        return None
    delta = output / f'tracesoul2-{runtime}-v{version}-from-v{previous["version"]}.delta.zip'
    with zipfile.ZipFile(delta, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(INVENTORY, raw)
        archive.writestr(DELTA, json.dumps(dict(schema=1, product='TraceSoul2', runtime=runtime,
            version=version, baseVersion=previous['version'])))
        for file in changed:
            archive.write(package / file['path'], file['path'])
    write_hash(delta)
    return delta


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for name in ('package', 'output', 'runtime', 'version'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--base')
    args = parser.parse_args()
    patch = build(args.package, args.output, args.runtime, args.version, args.base)
    print('File inventory generated; ' + ('incremental ZIP: ' + patch.name if patch else 'full package only.'))
