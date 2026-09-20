import importlib.util
import json
from pathlib import Path
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("qq_probe", Path(__file__).parents[1] / "probe_qq_call_bridge.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class ProbeTests(unittest.TestCase):
    def test_loopback_only(self):
        for url in ("http://example.com:6110", "http://localhost:6110", "http://127.0.0.1:6110" + "@evil.test",
                    "http://127.0.0.1:6110/?token=secret", "https://127.0.0.1:6110", "http://127.0.0.1"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                probe.validate_base_url(url)
        self.assertEqual(probe.validate_base_url("http://[::1]:6110/"), "http://[::1]:6110")

    def test_redaction_and_readiness(self):
        secret = "bridge-secret-" + "x" * 32

        def response(base_url, path, token=""):
            if path == "/healthz":
                return {"ok": True}, {"ok": True}
            return {"ok": True}, {"code": 0, "data": {
                "listenerRegistered": True, "serviceAvailable": True,
                "call": {"peerUid": "private-user"}, "recentEvents": [secret],
            }}

        def devices(server, kind):
            return {"maibot_qq_speaker.monitor", "maibot_qq_mic_source"} if kind == "sources" else {"maibot_qq_mic"}

        with patch.object(probe, "get_json", side_effect=response), patch.object(probe, "pulse_devices", side_effect=devices), \
                patch.object(probe.shutil, "which", return_value="present"), patch.object(probe.sys, "platform", "linux"):
            result = probe.probe("http://127.0.0.1:6110", "http://127.0.0.1:6111", secret)
        self.assertTrue(result["prerequisites_ready"])
        self.assertFalse(result["real_call_verified"])
        self.assertNotIn(secret, json.dumps(result))
        self.assertNotIn("private-user", json.dumps(result))

    def test_auth_missing_is_not_ready(self):
        with patch.object(probe, "get_json", return_value=({"ok": True}, {"ok": True})), \
                patch.object(probe, "pulse_devices", return_value=set()):
            result = probe.probe("http://127.0.0.1:6110", "http://127.0.0.1:6111", "")
        self.assertFalse(result["prerequisites_ready"])
        self.assertEqual(result["authenticated_status"]["reason"], "missing_token")

    def test_redirect_cannot_forward_token(self):
        calls = []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                calls.append(self.path)
                self.send_response(302)
                self.send_header("Location", "/unexpected")
                self.end_headers()

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            status, data = probe.get_json("http://127.0.0.1:" + str(server.server_port), "/v1/status", "test-token")
            self.assertFalse(status["ok"])
            self.assertIsNone(data)
            self.assertEqual(calls, ["/v1/status"])
        finally:
            server.shutdown()
            server.server_close()
            worker.join()


if __name__ == "__main__":
    unittest.main()
