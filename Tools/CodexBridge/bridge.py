from contextlib import closing
"""Loopback Chat Completions adapter for bounded, stateless Codex text jobs."""
import argparse
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class BridgeError(Exception):
    def __init__(self, status, code):
        self.status, self.code = status, code


def resolve_codex(executable):
    """Recover a removed desktop bundle path without searching arbitrary folders.

    Resolve per request: the supervisor's PATH can outlive an app update.
    Explicit non-desktop paths remain pinned and fail visibly when removed.
    """
    path = Path(executable)
    if path.is_file():
        return str(path)
    local = os.environ.get('LOCALAPPDATA')
    if local:
        bundles = Path(local) / 'OpenAI' / 'Codex' / 'bin'
        if (path.name.lower() == 'codex.exe' and
                re.fullmatch(r'[0-9a-fA-F]{16,64}', path.parent.name) and
                path.parent.parent.resolve() == bundles.resolve()):
            candidates = [p for p in bundles.glob('*/codex.exe')
                          if re.fullmatch(r'[0-9a-fA-F]{16,64}', p.parent.name) and p.is_file()]
            if candidates:
                return str(max(candidates, key=lambda p: p.stat().st_mtime_ns))
    if not path.is_absolute() and path.parent == Path('.'):
        found = shutil.which(executable)
        if found:
            return found
    raise BridgeError(503, 'codex_executable_not_found')


def validate(body, models):
    if not isinstance(body, dict) or body.get('model') not in models:
        raise BridgeError(400, 'model_not_allowed')
    if body.get('stream') or body.get('tools') or body.get('functions'):
        raise BridgeError(400, 'only_nonstreaming_text_supported')
    messages = body.get('messages')
    if not isinstance(messages, list) or not messages:
        raise BridgeError(400, 'messages_required')
    for message in messages:
        if (not isinstance(message, dict) or message.get('role') not in
                ('system', 'developer', 'user', 'assistant') or
                not isinstance(message.get('content'), str)):
            raise BridgeError(400, 'only_text_messages_supported')
    form = body.get('response_format') or {'type': 'text'}
    if not isinstance(form, dict) or form.get('type') not in ('text', 'json_object'):
        raise BridgeError(400, 'unsupported_response_format')
    return {'model': body['model'], 'messages': messages, 'response_format': form}


class CodexRunner:
    def __init__(self, executable, timeout=540):
        self.executable, self.timeout = executable, timeout

    def __call__(self, body):
        executable = resolve_codex(self.executable)
        # No project directory, project instructions, user config or session reuse.
        with tempfile.TemporaryDirectory(prefix='tracesoul-codex-') as folder:
            root = Path(folder)
            instructions = root / 'instructions.txt'
            system = '\n\n'.join(m['content'] for m in body['messages']
                                   if m['role'] in ('system', 'developer'))
            instructions.write_text(
                'You are the text inference backend for TraceSoul. Apply the following '
                'application instructions to the supplied conversation. Return only the '
                'next assistant response. Do not use tools or inspect files.\n\n' + system,
                encoding='utf-8')
            output = root / 'answer.txt'
            prompt = 'Conversation (ordered messages with their original roles):\n' + json.dumps(
                [m for m in body['messages'] if m['role'] not in ('system', 'developer')],
                ensure_ascii=False)
            if body['response_format']['type'] == 'json_object':
                prompt += '\nReturn one valid JSON object only, without Markdown fences.'
            command = [executable, '--no-daemon', '-a', 'never', 'exec',
                       '--ignore-user-config', '--ephemeral', '--skip-git-repo-check',
                       '--sandbox', 'read-only', '--cd', folder, '--model', body['model'],
                       '--json', '--output-last-message', str(output)]
            for setting in ('features.shell_tool=false', 'features.apps=false',
                            'features.remote_plugin=false', 'web_search="disabled"',
                            'project_doc_max_bytes=0',
                            'model_instructions_file=' + json.dumps(str(instructions))):
                command += ['-c', setting]
            command.append('-')
            try:
                proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding='utf-8',
                                        errors='replace', cwd=folder,
                                        creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0),
                                        start_new_session=os.name != 'nt')
            except FileNotFoundError:
                raise BridgeError(503, 'codex_executable_not_found') from None
            except OSError:
                raise BridgeError(503, 'codex_launch_failed') from None
            try:
                stdout, _ = proc.communicate(prompt, timeout=self.timeout)
            except subprocess.TimeoutExpired:
                if os.name == 'nt':
                    subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'],
                                   capture_output=True,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
                else:
                    import signal
                    os.killpg(proc.pid, signal.SIGKILL)
                proc.communicate()
                raise BridgeError(504, 'codex_timeout')
            if proc.returncode != 0:
                raise BridgeError(502, 'codex_failed_check_login_or_quota')
            events = []
            for line in stdout.splitlines():
                try:
                    events.append(json.loads(line))
                except ValueError:
                    continue
            if not any(e.get('type') == 'turn.completed' for e in events):
                raise BridgeError(502, 'codex_completion_unconfirmed')
            result = output.read_text(encoding='utf-8').strip() if output.exists() else ''
            if not result:
                raise BridgeError(502, 'codex_empty_output')
            if body['response_format']['type'] == 'json_object':
                try:
                    if not isinstance(json.loads(result), dict):
                        raise ValueError()
                except ValueError:
                    raise BridgeError(502, 'codex_invalid_json')
            return result


class Jobs:
    """Persist only result/status and input hash. Never persist the input prompt.

    Same input reuses success for ten minutes. Failed/unknown work is held for
    sixty seconds; interrupted work waits for the child deadline plus a minute.
    This bounds immediate HTTP retry storms, not exactly-once inference.
    """
    def __init__(self, path, runner, concurrency=1):
        self.path, self.runner = str(path), runner
        self.running_ttl = max(240, getattr(runner, 'timeout', 180) + 60)
        if not 1 <= concurrency <= 8:
            raise ValueError('Concurrency must be between 1 and 8')
        self.gate = threading.BoundedSemaphore(concurrency)
        with closing(sqlite3.connect(self.path)) as db, db:
            db.execute('CREATE TABLE IF NOT EXISTS jobs '
                       '(id TEXT PRIMARY KEY, status TEXT, result TEXT, updated REAL)')

    def complete(self, body):
        key = hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode()).hexdigest()
        if not self.gate.acquire(blocking=False):
            raise BridgeError(429, 'codex_busy')
        try:
            with closing(sqlite3.connect(self.path)) as db, db:
                # Reserve the hash atomically across concurrent HTTP workers.
                db.execute('BEGIN IMMEDIATE')
                row = db.execute('SELECT status,result,updated FROM jobs WHERE id=?', (key,)).fetchone()
                ttl = {'done': 600, 'running': self.running_ttl, 'failed': 60}
                if row and time.time() - row[2] < ttl.get(row[0], 240):
                    if row[0] == 'done':
                        return row[1]
                    raise BridgeError(503, 'codex_retry_cooldown')
                db.execute('DELETE FROM jobs WHERE updated < ?', (time.time() - max(600, self.running_ttl),))
                db.execute('INSERT OR REPLACE INTO jobs VALUES (?,?,?,?)',
                           (key, 'running', '', time.time()))
            try:
                result = self.runner(body)
            except Exception:
                with closing(sqlite3.connect(self.path)) as db, db:
                    db.execute('UPDATE jobs SET status=?,updated=? WHERE id=?', ('failed', time.time(), key))
                raise
            with closing(sqlite3.connect(self.path)) as db, db:
                db.execute('UPDATE jobs SET status=?,result=?,updated=? WHERE id=?',
                           ('done', result, time.time(), key))
            return result
        finally:
            self.gate.release()


def handler(jobs, token, models):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass  # Never log prompts, URLs with query data, or credentials.

        def setup(self):
            super().setup()
            self.connection.settimeout(240)

        def reply(self, status, body):
            data = json.dumps(body, ensure_ascii=False).encode()
            self.send_response(status)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.send_header('Content-Length', str(len(data)))
            if status in (429, 503):
                self.send_header('Retry-After', '60')
            self.end_headers()
            self.wfile.write(data)

        def authorized(self):
            if not hmac.compare_digest(self.headers.get('Authorization', '').encode(), ('Bearer ' + token).encode()):
                self.reply(401, {'error': {'message': 'unauthorized'}})
                return False
            return True

        def do_GET(self):
            if not self.authorized():
                return
            if self.path == '/v1/models':
                self.reply(200, {'object': 'list', 'data': [
                    {'id': m, 'object': 'model', 'owned_by': 'local-codex'} for m in models]})
            else:
                self.reply(404, {'error': {'message': 'not_found'}})

        def do_POST(self):
            if not self.authorized():
                return
            try:
                if self.path != '/v1/chat/completions':
                    raise BridgeError(404, 'not_found')
                length = int(self.headers.get('Content-Length', '0'))
                if length <= 0 or length > 2_000_000:
                    raise BridgeError(413, 'invalid_request_size')
                body = validate(json.loads(self.rfile.read(length)), models)
                answer = jobs.complete(body)
                self.reply(200, {'object': 'chat.completion', 'model': body['model'],
                                'choices': [{'index': 0, 'finish_reason': 'stop',
                                             'message': {'role': 'assistant', 'content': answer}}]})
            except BridgeError as error:
                print(json.dumps({'event': 'request_failed', 'time': time.time(),
                                  'status': error.status, 'code': error.code}), flush=True)
                self.reply(error.status, {'error': {'message': error.code}})
            except (ValueError, UnicodeError):
                self.reply(400, {'error': {'message': 'invalid_json'}})
            except (BrokenPipeError, ConnectionResetError):
                pass  # Completed results are saved before sending the response.
            except Exception as error:
                # Record the exception class, never its message, request or paths.
                print(json.dumps({'event': 'request_failed', 'time': time.time(),
                                  'status': 500, 'error_type': type(error).__name__}), flush=True)
                self.reply(500, {'error': {'message': 'bridge_internal_error'}})
    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=5091)
    parser.add_argument('--model', action='append', required=True)
    parser.add_argument('--state-dir', type=Path, required=True)
    parser.add_argument('--codex', default=shutil.which('codex'))
    parser.add_argument('--concurrency', type=int, choices=range(1, 9), default=1)
    args = parser.parse_args()
    token = os.environ.get('TRACESOUL_CODEX_BRIDGE_TOKEN', '')
    if len(token) < 32 or not args.codex:
        parser.error('Set a bridge-only token of at least 32 characters and install Codex CLI.')
    args.state_dir.mkdir(parents=True, exist_ok=True)
    # Hold an OS lock so a second process cannot run the same journal concurrently.
    with (args.state_dir / 'bridge.lock').open('a+b') as lock:
        lock.seek(0)
        if os.name == 'nt':
            import msvcrt
            lock.write(b'0'); lock.flush(); lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        jobs = Jobs(args.state_dir / 'jobs.sqlite', CodexRunner(args.codex), args.concurrency)
        server = ThreadingHTTPServer(('127.0.0.1', args.port), handler(jobs, token, args.model))
        print('Codex bridge listening on loopback; configured model list is not an access check.', flush=True)
        try:
            server.serve_forever()
        finally:
            server.server_close()


if __name__ == '__main__':
    main()
