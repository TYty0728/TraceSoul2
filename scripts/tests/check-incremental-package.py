"""Verify Python release output with the production C# materializer on a real package."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

repo = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(repo / 'scripts'))
from build_incremental import build, digest, INVENTORY


def main():
    package, checker = map(Path, sys.argv[1:])
    with tempfile.TemporaryDirectory(prefix='tracesoul2-delta-package-') as temporary:
        root = Path(temporary)
        base, target, output, result = (root / name for name in ('base', 'target', 'assets', 'result'))
        with zipfile.ZipFile(package) as archive:
            archive.extractall(base)
            archive.extractall(target)
        inventory = json.loads((target / INVENTORY).read_bytes())
        version, runtime = inventory['version'], inventory['runtime']
        marker = base / 'tracesoul2.install.json'
        manifest = json.loads(marker.read_bytes()); manifest['version'] = '0.0.1'
        marker.write_text(json.dumps(manifest), encoding='utf-8')
        (base / 'obsolete-fixture.txt').write_text('remove on update')
        build(base, output, runtime, '0.0.1')
        baseline = output / f'tracesoul2-{runtime}-v0.0.1.files.json'
        delta = build(target, output, runtime, version, baseline)
        assert delta is not None
        with zipfile.ZipFile(delta) as archive:
            assert not any(name.endswith('.onnx') for name in archive.namelist()), 'Unchanged model must not be downloaded'
        subprocess.run(['dotnet', str(checker), '--materialize-incremental', str(delta), str(base), str(result),
                        '0.0.1', version, runtime], check=True)
        expected = {file['path'] for file in inventory['files']} | {INVENTORY}
        assert {p.relative_to(result).as_posix() for p in result.rglob('*') if p.is_file()} == expected
        for file in inventory['files']:
            assert digest(result / file['path']) == file['sha256'], file['path']
        assert (base / 'obsolete-fixture.txt').exists(), 'Source installation was modified'
        print(f'Real package incremental round trip passed: {len(inventory["files"])} files; '
              f'fixture delta {delta.stat().st_size} bytes / full ZIP {package.stat().st_size} bytes.')


if __name__ == '__main__':
    main()
