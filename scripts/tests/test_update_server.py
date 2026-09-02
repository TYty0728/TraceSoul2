import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("update_server", Path(__file__).parents[1] / "update-server.py")
updater = importlib.util.module_from_spec(spec)
spec.loader.exec_module(updater)


class UpdateServerTests(unittest.TestCase):
    def test_crlf_and_hash_rejection(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package, sha = root / "test.zip", root / "test.zip.sha256"
            package.write_bytes(b"verified payload")
            sha.write_bytes((hashlib.sha256(package.read_bytes()).hexdigest() + "  test.zip\r\n").encode())
            updater.verify(package, sha)
            package.write_bytes(b"tampered")
            with self.assertRaises(ValueError):
                updater.verify(package, sha)
            self.assertFalse(package.exists())
            self.assertEqual(len(list(root.glob("test.zip.invalid-*"))), 1)

    def test_extract_refuses_traversal_and_symlink(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ["../escape", "/absolute", "..\\escape"]:
                package = root / "test.zip"
                with zipfile.ZipFile(package, "w") as archive:
                    archive.writestr(name, "bad")
                with self.assertRaises(ValueError):
                    updater.safe_extract(package, root / "stage")
            link = zipfile.ZipInfo("link")
            link.external_attr = 0o120777 << 16
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr(link, "../elsewhere")
            with self.assertRaises(ValueError):
                updater.safe_extract(package, root / "stage")

    def test_exact_asset_selection(self):
        release = {"tag_name": "v0.1.7", "assets": [
            {"name": "tracesoul2-linux-x64-v0.1.7.zip", "id": 1, "size": 100},
            {"name": "tracesoul2-linux-x64-v0.1.7.zip.sha256", "id": 2, "size": 99}]}
        version, pair = updater.select_assets(release, "linux-x64")
        self.assertEqual(version, "0.1.7")
        self.assertEqual(pair[0]["id"], 1)
        with self.assertRaises(KeyError):
            updater.select_assets(release, "linux-arm64")
        release["prerelease"] = True
        with self.assertRaises(ValueError):
            updater.select_assets(release, "linux-x64")

    def test_kill_uses_shell_builtin_and_validates_pid(self):
        with patch.object(updater, "docker") as docker:
            updater.stop_host("tracesoul2", "11")
            docker.assert_called_once_with("tracesoul2", "sh", "-c", 'kill -TERM "$1"', "sh", "11")
            with self.assertRaises(ValueError):
                updater.stop_host("tracesoul2", "1")
            with self.assertRaises(ValueError):
                updater.stop_host("tracesoul2", "11; shutdown")

    def test_pid_scanner_has_no_embedded_null_and_filters_dotnet(self):
        with patch.object(updater, "docker", return_value="11") as docker:
            self.assertEqual(updater.host_pid("tracesoul2"), "11")
            command = docker.call_args.args[-1]
            self.assertNotIn("\0", command)
            self.assertIn(r"\000", command)
            self.assertIn('"dotnet"', command)

    def test_download_resume_and_ignored_range(self):
        class Response(io.BytesIO):
            def __init__(self, data, status, headers):
                super().__init__(data)
                self.status, self.headers = status, headers
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "download.zip"
            path.write_bytes(b"abcd")
            def resume(request, **kwargs):
                self.assertEqual(request.headers["Range"], "bytes=4-")
                self.assertEqual(request.headers["Accept"], "application/octet-stream")
                return Response(b"efgh", 206, {"Content-Range": "bytes 4-7/8"})
            with patch.object(updater.urllib.request, "urlopen", side_effect=resume):
                updater.download("https://api.github.com/asset", path, 8)
            self.assertEqual(path.read_bytes(), b"abcdefgh")
            path.write_bytes(b"abcd")
            with patch.object(updater.urllib.request, "urlopen", return_value=Response(b"abcdefgh", 200, {"Content-Length": "8"})):
                updater.download("https://api.github.com/asset", path, 8)
            self.assertEqual(path.read_bytes(), b"abcdefgh")

    def test_bad_range_preserves_partial(self):
        class Response(io.BytesIO):
            status = 206
            headers = {"Content-Range": "bytes 3-7/8"}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "download.zip"
            path.write_bytes(b"abcd")
            with patch.object(updater.urllib.request, "urlopen", return_value=Response(b"defgh")):
                with self.assertRaises(ValueError):
                    updater.download("https://api.github.com/asset", path, 8)
            self.assertEqual(path.read_bytes(), b"abcd")


if __name__ == "__main__":
    unittest.main()
