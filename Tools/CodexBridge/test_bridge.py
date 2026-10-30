from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import patch
from bridge import BridgeError, CodexRunner, Jobs, handler, validate, resolve_codex


BODY = {'model': 'test', 'messages': [{'role': 'user', 'content': 'synthetic source'}],
        'response_format': {'type': 'json_object'}}


class BridgeTests(unittest.TestCase):
    def test_removed_desktop_binary_is_resolved_again_after_each_update(self):
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, {'LOCALAPPDATA': folder}):
            bundles = Path(folder) / 'OpenAI' / 'Codex' / 'bin'
            old = bundles / ('a' * 16) / 'codex.exe'
            current = bundles / ('b' * 16) / 'codex.exe'
            current.parent.mkdir(parents=True); current.touch()
            runner = CodexRunner(str(old))
            calls = []
            class FakeProcess:
                returncode = 0
                def __init__(self, command, **kwargs):
                    calls.append(command[0])
                    Path(command[command.index('--output-last-message') + 1]).write_text('{"ok":true}')
                def communicate(self, *args, **kwargs):
                    return '{"type":"turn.completed"}', ''
            with patch('bridge.subprocess.Popen', FakeProcess):
                self.assertEqual(runner(BODY), '{"ok":true}')
                current.unlink()
                newer = bundles / ('c' * 16) / 'codex.exe'
                newer.parent.mkdir(); newer.touch()
                self.assertEqual(runner(BODY), '{"ok":true}')
            self.assertEqual(calls, [str(current), str(newer)])
            # A separately pinned install must not silently switch installations.
            with self.assertRaises(BridgeError) as error:
                resolve_codex(str(Path(folder) / 'custom' / 'codex.exe'))
            self.assertEqual(error.exception.code, 'codex_executable_not_found')
            newer.unlink()
            with self.assertRaises(BridgeError) as error:
                resolve_codex(str(old))
            self.assertEqual(error.exception.status, 503)

    def test_launch_errors_are_explicit_without_private_paths(self):
        for failure, code in [(FileNotFoundError('PRIVATE path'), 'codex_executable_not_found'),
                              (PermissionError('PRIVATE path'), 'codex_launch_failed')]:
            with patch('bridge.resolve_codex', return_value='fake'), \
                    patch('bridge.subprocess.Popen', side_effect=failure):
                with self.assertRaises(BridgeError) as error:
                    CodexRunner('fake')(BODY)
                self.assertEqual(error.exception.status, 503)
                self.assertEqual(error.exception.code, code)
                self.assertNotIn('PRIVATE', str(error.exception))

    def test_saved_result_survives_restart_without_repeating_inference(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'jobs.sqlite'
            calls = []
            def run(body):
                calls.append(body)
                return '{"ok":true}'
            self.assertEqual(Jobs(path, run).complete(BODY), '{"ok":true}')
            self.assertEqual(Jobs(path, run).complete(BODY), '{"ok":true}')
            self.assertEqual(len(calls), 1)
            changed = dict(BODY, messages=[{'role': 'user', 'content': 'changed source'}])
            Jobs(path, run).complete(changed)
            self.assertEqual(len(calls), 2)

    def test_failure_and_interrupted_work_do_not_retry_immediately(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'jobs.sqlite'
            calls = []
            def fail(body):
                calls.append(body)
                raise BridgeError(504, 'timeout')
            for _ in range(2):
                with self.assertRaises(BridgeError):
                    Jobs(path, fail).complete(BODY)
            self.assertEqual(len(calls), 1)
            with closing(sqlite3.connect(path)) as db, db:
                db.execute("UPDATE jobs SET status='running'")
            with self.assertRaises(BridgeError):
                Jobs(path, fail).complete(BODY)
            self.assertEqual(len(calls), 1)

    def test_http_auth_contract_and_no_prompt_leak_on_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            jobs = Jobs(Path(folder) / 'jobs.sqlite', lambda _: '{"ok":true}')
            server = ThreadingHTTPServer(('127.0.0.1', 0), handler(jobs, 'test-token', ['test']))
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            url = 'http://127.0.0.1:' + str(server.server_port)
            try:
                with self.assertRaises(urllib.error.HTTPError) as error:
                    urllib.request.urlopen(url + '/v1/models')
                self.assertEqual(error.exception.code, 401)
                request = urllib.request.Request(url + '/v1/chat/completions',
                    data=json.dumps(BODY).encode(), headers={'Authorization': 'Bearer test-token'})
                with urllib.request.urlopen(request) as response:
                    self.assertEqual(json.load(response)['choices'][0]['message']['content'], '{"ok":true}')
                jobs.runner = lambda _: (_ for _ in ()).throw(RuntimeError('PRIVATE PROMPT'))
                request.data = json.dumps(dict(BODY, messages=[{'role': 'user', 'content': 'other'}])).encode()
                with self.assertRaises(urllib.error.HTTPError) as error:
                    urllib.request.urlopen(request)
                self.assertNotIn(b'PRIVATE', error.exception.read())
            finally:
                server.shutdown(); server.server_close(); thread.join()

    def test_reject_unsupported_inputs(self):
        for body in [dict(BODY, stream=True), dict(BODY, model='unlisted'),
                     dict(BODY, messages=[{'role': 'user', 'content': []}]),
                     dict(BODY, tools=[{}]), dict(BODY, response_format={'type': 'json_schema'})]:
            with self.assertRaises(BridgeError):
                validate(body, ['test'])

    def test_busy_bridge_does_not_launch_another_model(self):
        with tempfile.TemporaryDirectory() as folder:
            calls = []
            jobs = Jobs(Path(folder) / 'jobs.sqlite', lambda b: calls.append(b))
            with jobs.gate:
                with self.assertRaises(BridgeError) as error:
                    jobs.complete(BODY)
                self.assertEqual(error.exception.status, 429)
            self.assertEqual(calls, [])

    def test_concurrent_distinct_jobs_and_duplicate_reservation(self):
        from concurrent.futures import ThreadPoolExecutor
        with tempfile.TemporaryDirectory() as folder:
            entered = threading.Barrier(3)
            release = threading.Event()
            calls = []
            def run(body):
                calls.append(body)
                entered.wait(timeout=5)
                release.wait(timeout=5)
                return '{"ok":true}'
            jobs = Jobs(Path(folder) / 'jobs.sqlite', run, concurrency=3)
            different = dict(BODY, messages=[{'role': 'user', 'content': 'different'}])
            with ThreadPoolExecutor(max_workers=2) as pool:
                first = pool.submit(jobs.complete, BODY)
                second = pool.submit(jobs.complete, different)
                try:
                    entered.wait(timeout=5)
                    with self.assertRaises(BridgeError) as error:
                        jobs.complete(BODY)
                    self.assertEqual(error.exception.status, 503)
                    self.assertEqual(len(calls), 2)
                finally:
                    release.set()
                self.assertEqual(first.result(), '{"ok":true}')
                self.assertEqual(second.result(), '{"ok":true}')
            self.assertEqual(jobs.complete(BODY), '{"ok":true}')
            self.assertEqual(len(calls), 2)

    def test_cli_partial_and_invalid_json_are_never_success(self):
        for output, events, returncode in [('{"ok":true}', '', 0), ('not json', '{"type":"turn.completed"}', 0),
                                           ('{"ok":true}', '{"type":"turn.completed"}', 1)]:
            class FakeProcess:
                def __init__(self, command, **kwargs):
                    self.returncode = returncode
                    Path(command[command.index('--output-last-message') + 1]).write_text(output)
                def communicate(self, *args, **kwargs):
                    return events, ''
            with patch('bridge.resolve_codex', return_value='fake'), patch('bridge.subprocess.Popen', FakeProcess):
                with self.assertRaises(BridgeError):
                    CodexRunner('fake')(BODY)


if __name__ == '__main__':
    unittest.main()
