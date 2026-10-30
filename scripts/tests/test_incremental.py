"""Release inventory/delta contracts; fixtures contain no application/user data."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from build_incremental import build, digest, safe_path, validate, INVENTORY, DELTA
from fetch_incremental_base import fetch


class IncrementalTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / 'package'
        self.package.mkdir()
        (self.package / 'model.bin').write_bytes(bytes(range(256)) * 1024)
        (self.package / 'host.dll').write_bytes(b'old host')
        (self.package / 'removed.txt').write_text('obsolete')
        self.output = self.root / 'output'
        build(self.package, self.output, 'linux-x64', '1.0.0')
        self.base = self.output / 'tracesoul2-linux-x64-v1.0.0.files.json'

    def test_delta_reuses_model_and_excludes_removed_files(self):
        (self.package / 'host.dll').write_bytes(b'new host')
        (self.package / 'removed.txt').unlink()
        (self.package / 'new.txt').write_text('new')
        delta = build(self.package, self.output, 'linux-x64', '1.0.1', self.base)
        with zipfile.ZipFile(delta) as archive:
            self.assertEqual(set(archive.namelist()), {INVENTORY, DELTA, 'host.dll', 'new.txt'})
            inventory = json.loads(archive.read(INVENTORY))
            self.assertEqual({f['path'] for f in inventory['files']}, {'model.bin', 'host.dll', 'new.txt'})
            for file in inventory['files']:
                self.assertEqual(file['sha256'], digest(self.package / file['path']))
            self.assertEqual(json.loads(archive.read(DELTA))['baseVersion'], '1.0.0')
        self.assertEqual(delta.with_name(delta.name + '.sha256').read_text().split()[0], digest(delta))
        self.assertLess(delta.stat().st_size, (self.package / 'model.bin').stat().st_size // 10)

    def test_full_inventory_without_base_and_same_version(self):
        self.assertIsNone(build(self.package, self.output, 'linux-x64', '1.0.0', self.base))
        self.assertIsNone(build(self.package, self.output, 'linux-x64', '1.0.1', self.root / 'missing'))
        validate(json.loads((self.package / INVENTORY).read_bytes()), 'linux-x64')

    def test_inventory_rejects_duplicate_and_wrong_platform(self):
        manifest = json.loads(self.base.read_bytes())
        with self.assertRaises(ValueError): validate(manifest, 'win-x64')
        manifest['files'].append(dict(manifest['files'][0], path=manifest['files'][0]['path'].upper()))
        with self.assertRaises(ValueError): validate(manifest, 'linux-x64')

    def test_portable_paths(self):
        for value in ('../a', '/a', 'a//b', 'a\\b', 'C:a', 'a/CON.txt', 'a.', 'a ', 'a?b', 'a\x00b'):
            with self.subTest(value=value): self.assertFalse(safe_path(value))
        self.assertTrue(safe_path('BundledPlugins/plugin/config.json'))

    def test_fetch_offline_clears_stale_baselines(self):
        (self.output / 'linux-x64.json').write_bytes(self.base.read_bytes())
        with patch('urllib.request.urlopen', side_effect=OSError('offline')):
            fetch('test/repo', self.output)
        self.assertFalse((self.output / 'linux-x64.json').exists())

    def test_fetch_checks_digest(self):
        raw = self.base.read_bytes()
        name = self.base.name
        release = json.dumps(dict(tag_name='v1.0.0', assets=[{'name': name}, {'name': name + '.sha256'}])).encode()
        import io
        for checksum, expected in ((b'0' * 64, False), (hashlib.sha256(raw).hexdigest().encode(), True)):
            with patch('urllib.request.urlopen', side_effect=[io.BytesIO(release), io.BytesIO(raw), io.BytesIO(checksum)]):
                fetch('test/repo', self.output)
            self.assertEqual((self.output / 'linux-x64.json').exists(), expected)


if __name__ == '__main__':
    unittest.main()
