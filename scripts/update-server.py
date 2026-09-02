#!/usr/bin/env python3
"""One-time Docker updater for old WebUI versions. Python 3 standard library only."""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zipfile


def get_json(url):
    for attempt in range(4):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "TraceSoul2-SSH-Updater",
                                                          "Accept": "application/vnd.github+json"})
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
        except (OSError, urllib.error.URLError):
            if attempt == 3:
                raise
            time.sleep(2 * (attempt + 1))


def select_assets(release, runtime):
    version = release.get("tag_name", "")
    if version.startswith("v"):
        version = version[1:]
    if not re.fullmatch(r"\d+\.\d+\.\d+", version) or release.get("draft") or release.get("prerelease"):
        raise ValueError("需要正式的 vMAJOR.MINOR.PATCH Release。")
    name = "tracesoul2-" + runtime + "-v" + version + ".zip"
    assets = {a["name"]: a for a in release.get("assets", [])}
    pair = [assets[name], assets[name + ".sha256"]]
    for asset, limit in zip(pair, [2 * 1024 ** 3, 65536]):
        if not isinstance(asset.get("id"), int) or asset["id"] <= 0 or not 0 < asset.get("size", 0) <= limit:
            raise ValueError("Release 资产 ID/大小无效。")
    return version, pair


def quarantine(path):
    if path.exists():
        path.rename(path.with_name(path.name + ".invalid-" + uuid.uuid4().hex))


def download(url, path, size):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and path.stat().st_size > size:
        quarantine(path)
    for attempt in range(1, 9):
        offset = path.stat().st_size if path.exists() else 0
        if offset == size:
            print("使用完整缓存，稍后校验：", path.name, flush=True)
            return
        print("下载 {}，第 {}/8 次，从 {} 字节继续".format(path.name, attempt, offset), flush=True)
        headers = {"User-Agent": "TraceSoul2-SSH-Updater", "Accept": "application/octet-stream", "Cache-Control": "no-cache"}
        if offset:
            headers["Range"] = "bytes={}-".format(offset)
        try:
            # urllib uses HTTP/1.1; timeout bounds socket operations, not the total transfer time.
            with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=45) as response:
                if "json" in response.headers.get("Content-Type", ""):
                    raise OSError("资产 API 返回元数据，正在重试。")
                if response.status == 206:
                    match = re.fullmatch(r"bytes (\d+)-(\d+)/(\d+)", response.headers.get("Content-Range", ""))
                    if not match or int(match[1]) != offset or int(match[3]) != size or not offset <= int(match[2]) < size:
                        raise ValueError("续传响应范围错误，拒绝拼接。")
                elif response.status == 200:
                    length = response.headers.get("Content-Length")
                    if length and int(length) != size:
                        raise ValueError("远端文件大小与 Release 不一致。")
                    offset = 0
                else:
                    raise ValueError("资产下载响应状态错误。")
                with path.open("r+b" if path.exists() else "w+b") as output:
                    output.truncate(offset)
                    output.seek(offset)
                    shown = 0.0
                    while True:
                        block = response.read(65536)
                        if not block:
                            break
                        if offset + len(block) > size:
                            raise ValueError("下载内容超过预期大小。")
                        output.write(block)
                        offset += len(block)
                        if time.monotonic() - shown >= 1:
                            print("\r{:.1f}%  {:.1f}/{:.1f} MiB".format(offset * 100 / size, offset / 1024 ** 2, size / 1024 ** 2), end="", flush=True)
                            shown = time.monotonic()
                if offset != size:
                    raise OSError("连接提前结束")
                print("\n下载完成", flush=True)
                return
        except (OSError, urllib.error.URLError, http.client.HTTPException) as error:
            print("\n下载中断（{}），已保留文件，可续传。".format(type(error).__name__), flush=True)
            if isinstance(error, urllib.error.HTTPError) and error.code == 416:
                quarantine(path)
            if attempt == 8:
                raise RuntimeError("下载重试耗尽；重新执行此脚本将继续下载。") from error
            time.sleep(min(10, attempt * 2))


def verify(zip_path, sha_path):
    # split handles LF, CRLF and BOM; CR can never become part of the filename.
    text = sha_path.read_text(encoding="utf-8-sig").split()
    if not text or not re.fullmatch(r"[a-fA-F0-9]{64}", text[0]):
        quarantine(sha_path)
        raise ValueError("SHA-256 文件格式错误。")
    digest = hashlib.sha256()
    with zip_path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != text[0].lower():
        quarantine(zip_path)
        quarantine(sha_path)
        raise ValueError("SHA-256 校验失败，缓存已隔离；未修改运行中的程序。")
    print("SHA-256: OK", flush=True)


def safe_extract(zip_path, destination):
    root = destination.resolve()
    with zipfile.ZipFile(zip_path) as archive:
        if sum(info.file_size for info in archive.infolist()) > 4 * 1024 ** 3:
            raise ValueError("解压总大小超过 4 GiB。")
        for info in archive.infolist():
            target = (root / info.filename.replace("\\", "/")).resolve()
            if root not in target.parents or (info.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("安装包包含越界路径或符号链接。")
        archive.extractall(root)


def docker(container, *args):
    result = subprocess.run(["docker", "exec", container, *args], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError("Docker 命令失败：" + result.stderr.strip())
    return result.stdout.strip()


def host_pid(container):
    # Filter comm first: the scanner's own command line also contains the DLL name.
    result = docker(container, "sh", "-c", r'''
for p in /proc/[0-9]*; do
  [ "$(cat "$p/comm" 2>/dev/null)" = "dotnet" ] || continue
  cmd="$(tr '\000' ' ' < "$p/cmdline" 2>/dev/null)"
  case "$cmd" in *TraceSoul2.Host.dll*) basename "$p"; exit 0;; esac
done
exit 1
''')
    if not result.isdigit() or int(result) <= 1:
        raise ValueError("无法安全确认容器内 Host PID。")
    return result


def stop_host(container, pid):
    # slim images lack /bin/kill: use the shell builtin and pass PID as an argument.
    if not pid.isdigit() or int(pid) <= 1:
        raise ValueError("无效的 Host PID。")
    docker(container, "sh", "-c", 'kill -TERM "$1"', "sh", pid)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=os.getcwd(), help="contains compose.yaml and runtime/")
    parser.add_argument("--version", default="latest", help="0.1.7 or latest")
    parser.add_argument("--container", default="tracesoul2")
    args = parser.parse_args()
    root = Path(args.root).resolve()
    bundle = (root / "runtime").resolve()
    if not (root / "compose.yaml").is_file() or not (bundle / "App/tracesoul2.install.json").is_file():
        raise ValueError("--root 必须指向已经安装的 Docker 项目目录。")
    inspect = json.loads(subprocess.check_output(["docker", "inspect", args.container], text=True))[0]
    if not inspect["State"]["Running"]:
        raise ValueError("容器没有运行，请先启动现有服务。")
    if not any(m["Destination"] == "/opt/tracesoul2" and Path(m["Source"]).resolve() == bundle and m.get("RW") for m in inspect["Mounts"]):
        raise ValueError("容器挂载路径与 --root/runtime 不一致，拒绝更新。")
    env = dict(x.split("=", 1) for x in inspect["Config"]["Env"] if "=" in x)
    for name, expected in {"TRACESOUL2_HOME": "/opt/tracesoul2/Data", "TRACESOUL2_PLUGINS": "/opt/tracesoul2/Plugins",
                           "TRACESOUL2_RESTART_MODE": "supervisor"}.items():
        if env.get(name) != expected:
            raise ValueError("容器自定义了 {}，请不要使用默认路径更新脚本。".format(name))
    machine = docker(args.container, "uname", "-m")
    runtime = {"x86_64": "linux-x64", "aarch64": "linux-arm64", "arm64": "linux-arm64"}.get(machine)
    if not runtime:
        raise ValueError("暂不支持此 CPU 架构。")
    repository = "https://api.github.com/repos/TYty0728/TraceSoul2"
    tag = args.version[1:] if args.version.startswith("v") else args.version
    if tag != "latest" and not re.fullmatch(r"\d+\.\d+\.\d+", tag):
        raise ValueError("无效的 --version。")
    release = get_json(repository + ("/releases/latest" if tag == "latest" else "/releases/tags/v" + tag))
    version, assets = select_assets(release, runtime)
    current = json.loads((bundle / "App/tracesoul2.install.json").read_text(encoding="utf-8-sig"))["version"]
    if tuple(map(int, version.split("."))) <= tuple(map(int, current.split("."))):
        print("已安装 v{}，目标 v{} 无需重复安装。".format(current, version))
        return
    print("将从 v{} 升级到 v{}。请不要同时点击 WebUI 安装。".format(current, version), flush=True)
    import fcntl
    updates = bundle / "Data/updates"
    updates.mkdir(parents=True, exist_ok=True)
    lock = (updates / "server-bootstrap.lock").open("a")
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    cache = updates / "manual" / version
    paths = []
    for asset in assets:
        identity = str(asset["id"]) + "-" + str(asset["size"]) + "-" + hashlib.sha256(asset.get("updated_at", "").encode()).hexdigest()[:12]
        path = cache / identity / asset["name"]
        download(repository + "/releases/assets/" + str(asset["id"]), path, asset["size"])
        paths.append(path)
    verify(*paths)
    stage = Path(tempfile.mkdtemp(prefix=".App.tracesoul2-update-" + version + "-", dir=bundle))
    safe_extract(paths[0], stage)
    manifest = json.loads((stage / "tracesoul2.install.json").read_text(encoding="utf-8-sig"))
    if manifest.get("product") != "TraceSoul2" or manifest.get("version") != version or manifest.get("runtime") != runtime:
        raise ValueError("安装包清单不匹配，未停止旧程序。")
    runner_id = uuid.uuid4().hex
    runner = updates / "runner" / runner_id
    runner.mkdir(parents=True)
    for source in stage.glob("TraceSoul2.Updater*"):
        shutil.copy2(source, runner / source.name)
    if not (runner / "TraceSoul2.Updater.dll").is_file():
        raise ValueError("安装包缺少外置更新器。")
    pid = host_pid(args.container)
    log = updates / "update.log"
    offset = log.stat().st_size if log.exists() else 0
    command = ["docker", "exec", "-d", args.container, "dotnet",
               "/opt/tracesoul2/Data/updates/runner/" + runner_id + "/TraceSoul2.Updater.dll",
               "--pid", pid, "--source", "/opt/tracesoul2/" + stage.name,
               "--target", "/opt/tracesoul2/App", "--home", "/opt/tracesoul2/Data",
               "--plugins", "/opt/tracesoul2/Plugins", "--version", version]
    subprocess.run(command, check=True)

    def recent_log():
        if not log.exists():
            return ""
        with log.open("rb") as stream:
            stream.seek(offset)
            return stream.read().decode("utf-8", errors="replace")

    # Do not kill Host until the updater has passed its own path/package validation.
    for _ in range(20):
        time.sleep(0.5)
        recent = recent_log()
        if "更新失败" in recent:
            raise RuntimeError(recent)
        if "等待旧宿主退出" in recent:
            break
    else:
        raise RuntimeError("未确认更新器准备就绪；旧宿主未停止，请检查 Data/updates/update.log。")
    started = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    stop_host(args.container, pid)
    print("更新器已就绪，已请求旧宿主退出；正在等待新版启动…", flush=True)
    for _ in range(90):
        time.sleep(2)
        recent = recent_log()
        if "更新失败" in recent or "已回滚旧版" in recent:
            raise RuntimeError(recent)
        try:
            installed = json.loads((bundle / "App/tracesoul2.install.json").read_text(encoding="utf-8-sig"))
            if installed.get("version") != version or host_pid(args.container) == pid:
                continue
            logs = subprocess.run(["docker", "logs", "--since", started, "--tail", "200", args.container], capture_output=True, text=True)
            if "TraceSoul2 Host  v" + version not in logs.stdout + logs.stderr:
                continue
            print(recent)
            print("更新成功：v{} 已运行。数据、插件配置和旧版本备份均保留。请强制刷新 WebUI。".format(version))
            return
        except (OSError, ValueError, RuntimeError):
            pass
    raise RuntimeError("未能确认新版在三分钟内启动；请检查 Data/updates/update.log 和 docker compose logs，勿重复安装。")


if __name__ == "__main__":
    try:
        main()
    except (Exception, KeyboardInterrupt) as error:
        print("\n更新未完成：" + str(error), file=sys.stderr)
        print("下载缓存和旧版本备份均保留；不要删除 runtime/App 或 Data。", file=sys.stderr)
        sys.exit(1)
