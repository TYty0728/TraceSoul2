"""CI-only: use a real slim Docker container, supervisor and production updater."""
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
from unittest.mock import patch
import uuid

repo = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("bootstrap", repo / "scripts/update-server.py")
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)


def run(*args):
    subprocess.run(args, check=True)


def main():
    package, = (repo / "artifacts").glob("*/tracesoul2-linux-x64-v*.zip")
    sha = package.with_name(package.name + ".sha256")
    assert b"\r" not in sha.read_bytes(), "Release SHA file must use LF"
    version = package.parent.name
    assets = [
        {"name": package.name, "id": 1, "size": package.stat().st_size},
        {"name": sha.name, "id": 2, "size": sha.stat().st_size},
    ]
    image = "mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim"
    container = "tracesoul2-update-test-" + uuid.uuid4().hex[:10]
    run("docker", "pull", image)
    with tempfile.TemporaryDirectory(prefix="tracesoul2-integration-") as directory:
        root = Path(directory)
        bundle = root / "runtime"
        app = bundle / "App"
        for name in ["App", "Data", "Plugins/third-party", "plugins_data/qq-tts"]:
            (bundle / name).mkdir(parents=True)
        (root / "compose.yaml").write_text("# disposable integration fixture\n")
        fixture = root / "old-host"
        shutil.copytree(repo / "scripts/tests/fixtures/old-host", fixture)
        run("dotnet", "publish", str(fixture / "OldHost.csproj"), "-c", "Release", "-o", str(app))
        shutil.copy2(repo / "scripts/Start-TraceSoul2.sh", app / "Start-TraceSoul2.sh")
        (app / "tracesoul2.install.json").write_text(json.dumps({"product": "TraceSoul2", "version": "0.0.1", "runtime": "linux-x64"}))
        (bundle / "Data/keep.txt").write_text("character data stays")
        (bundle / "plugins_data/qq-tts/keep.txt").write_text("plugin settings stay")
        (bundle / "Plugins/third-party/keep.txt").write_text("third-party stays")
        env = {"TRACESOUL2_HOME": "/opt/tracesoul2/Data", "TRACESOUL2_PLUGINS": "/opt/tracesoul2/Plugins",
               "TRACESOUL2_PLUGINS_DATA": "/opt/tracesoul2/plugins_data", "TRACESOUL2_RESTART_MODE": "supervisor",
               "TRACESOUL2_URLS": "http://0.0.0.0:5080"}
        command = ["docker", "run", "-d", "--name", container, "--mount",
                   "type=bind,src=" + str(bundle) + ",dst=/opt/tracesoul2", "-w", "/opt/tracesoul2"]
        for key, value in env.items():
            command += ["-e", key + "=" + value]
        command += [image, "sh", "/opt/tracesoul2/App/Start-TraceSoul2.sh"]
        try:
            run(*command)
            time.sleep(3)
            def local_download(url, destination, size):
                source = package if url.endswith("/1") else sha
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, destination)
                assert destination.stat().st_size == size
            with patch.object(bootstrap, "get_json", return_value={"tag_name": "v" + version, "assets": assets}), \
                 patch.object(bootstrap, "download", side_effect=local_download), \
                 patch.object(sys, "argv", ["update-server.py", "--root", str(root), "--container", container, "--version", version]):
                bootstrap.main()
            assert json.loads((app / "tracesoul2.install.json").read_text())["version"] == version
            assert (bundle / "Data/keep.txt").read_text() == "character data stays"
            assert (bundle / "plugins_data/qq-tts/keep.txt").read_text() == "plugin settings stay"
            assert (bundle / "Plugins/third-party/keep.txt").read_text() == "third-party stays"
            for plugin in ["qq-tts", "qq-imagegen", "qq-qzone", "qq-status", "game-session"]:
                assert (bundle / "Plugins" / plugin / "plugin.json").is_file()
            assert len(list(bundle.glob(".App.tracesoul2-backup-0.0.1-*"))) == 1
            print("Real Docker bootstrap passed: new Host runs, 5 plugins upgraded, all persistent data and backups preserved.")
        finally:
            # Only this script's uniquely named disposable test container is removed.
            subprocess.run(["docker", "rm", "-f", container], check=False)
            # The test container runs as root and creates root-owned fixture files.
            subprocess.run(["sudo", "chown", "-R", str(__import__("os").getuid()), str(root)], check=False)


if __name__ == "__main__":
    main()
