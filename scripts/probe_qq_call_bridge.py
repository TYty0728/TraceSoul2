#!/usr/bin/env python3
"""Read-only probe, run INSIDE the QQ/AV bridge container. Never starts or accepts a call."""
import argparse
import ipaddress
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def validate_base_url(value):
    parsed = urllib.parse.urlsplit(value)
    try:
        address = ipaddress.ip_address(parsed.hostname or "")
        port = parsed.port
    except ValueError:
        raise ValueError("base_url_must_be_numeric_loopback") from None
    if (parsed.scheme != "http" or not address.is_loopback or parsed.username is not None
            or parsed.password is not None or parsed.path not in ("", "/")
            or parsed.query or parsed.fragment or port is None):
        raise ValueError("invalid_loopback_base_url")
    return value.rstrip("/")


def get_json(base_url, path, token=""):
    # Ignore proxy env and never redirect a request carrying the bridge token.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(base_url + path, headers=headers, method="GET")
    try:
        with opener.open(req, timeout=3) as response:
            raw = response.read(65537)
            if len(raw) > 65536:
                return {"ok": False, "reason": "response_too_large"}, None
            payload = json.loads(raw)
            if not isinstance(payload, dict):
                return {"ok": False, "reason": "invalid_response"}, None
            return {"ok": True}, payload
    except urllib.error.HTTPError as exc:
        return {"ok": False, "reason": "http_" + str(exc.code)}, None
    except (OSError, ValueError, urllib.error.URLError):
        return {"ok": False, "reason": "unreachable_or_invalid_response"}, None


def pulse_devices(server, kind):
    if not shutil.which("pactl"):
        return set()
    args = ["pactl"]
    if server:
        args += ["--server", server]
    args += ["list", "short", kind]
    try:
        result = subprocess.run(args, capture_output=True, text=True, timeout=3, check=False)
        if result.returncode != 0:
            return set()
        return {line.split()[1] for line in result.stdout.splitlines() if len(line.split()) >= 2}
    except (OSError, subprocess.TimeoutExpired):
        return set()


def probe(base_url, av_url, token, pulse_server="", qq_dir=None, avsdk_path=None):
    base_url, av_url = validate_base_url(base_url), validate_base_url(av_url)
    commands = {name: shutil.which(name) is not None
                for name in ("pactl", "parec", "pacat", "pulseaudio", "xvfb-run")}
    health, body = get_json(base_url, "/healthz")
    health["ok"] = health["ok"] and body.get("ok") is True if body else False
    av_health, av_body = get_json(av_url, "/healthz")
    av_health["ok"] = av_health["ok"] and av_body.get("ok") is True if av_body else False
    status = {"ok": False, "reason": "missing_token"}
    listener = service = False
    if token:
        if not 32 <= len(token) <= 4096 or any(c.isspace() for c in token):
            status = {"ok": False, "reason": "invalid_token_format"}
        else:
            status, data = get_json(base_url, "/v1/status", token)
            if data:
                content = data.get("data")
                status["ok"] = status["ok"] and data.get("code") == 0 and isinstance(content, dict)
                if status["ok"]:
                    listener = content.get("listenerRegistered") is True
                    service = content.get("serviceAvailable") is True
    sources = pulse_devices(pulse_server, "sources")
    sinks = pulse_devices(pulse_server, "sinks")
    audio = {
        "capture_source": "maibot_qq_speaker.monitor" in sources,
        "playback_sink": "maibot_qq_mic" in sinks,
        "qq_microphone_source": "maibot_qq_mic_source" in sources,
    }
    files = None
    if qq_dir:
        root = Path(qq_dir)
        files = {"loader": (root / "resources/app/loadNapCat.js").is_file(),
                 "avsdk": Path(avsdk_path).is_file() if avsdk_path else None}
    control_ready = health["ok"] and av_health["ok"] and status["ok"] and listener and service
    files_ready = files is None or all(value is not False for value in files.values())
    ready = control_ready and all(audio.values()) and all(commands.values()) and files_ready
    return {
        "probe": "qq_av_bridge_v1", "platform_linux": sys.platform.startswith("linux"),
        "commands": commands, "bridge_health": health, "av_host_health": av_health,
        "authenticated_status": status, "listener_registered": listener, "service_available": service,
        "audio_devices": audio, "expected_files": files,
        "control_ready": bool(control_ready), "prerequisites_ready": bool(ready and sys.platform.startswith("linux")),
        "real_call_verified": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bridge-url", default="http://127.0.0.1:6110")
    parser.add_argument("--av-host-url", default="http://127.0.0.1:6111")
    parser.add_argument("--token-file", help="Read a local bridge token file; its contents are never printed")
    parser.add_argument("--pulse-server", default=os.environ.get("PULSE_SERVER", ""))
    parser.add_argument("--qq-dir")
    parser.add_argument("--avsdk-path", help="Optional explicit AVSDK path; installation layout is version-dependent")
    args = parser.parse_args()
    try:
        token = os.environ.get("TRACESOUL2_QQ_BRIDGE_TOKEN", "")
        if args.token_file:
            with open(args.token_file, encoding="utf-8") as stream:
                token = stream.read(4097).strip()
        result = probe(args.bridge_url, args.av_host_url, token, args.pulse_server, args.qq_dir, args.avsdk_path)
    except (OSError, ValueError):
        print(json.dumps({"probe": "qq_av_bridge_v1", "error": "invalid_config_or_unreadable_token"}))
        return 2
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["prerequisites_ready"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
