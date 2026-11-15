"""Bounded rendezvous for outbound board connections; payload remains end-to-end TLS.

The relay access key is independent of the board TLS identity/control token.
Registration uses fresh HMAC challenges, never plaintext credentials. The relay
cannot authorize a board command: WifiLink still verifies its pinned certificate
before sending the control token inside TLS. No payload or peer address is logged.
"""
import argparse
import hashlib
import hmac
import json
import secrets
import select
import socket
import threading
import time


def line(sock, limit=160):
    result = bytearray()
    while len(result) < limit:
        block = sock.recv(1)
        if not block:
            raise ConnectionError('Relay disconnected')
        if block == b'\n':
            return bytes(result)
        result.extend(block)
    raise ValueError('Relay header too long')


def proof(key, role, nonce):
    return hmac.new(bytes.fromhex(key), b'TraceSoul relay v1\n' + role.encode() + b'\n' + nonce,
                    hashlib.sha256).hexdigest().encode()


def register(sock, key, role):
    nonce = line(sock)
    if len(nonce) != 64 or any(c not in b'0123456789abcdef' for c in nonce):
        raise ValueError('Invalid relay challenge')
    sock.sendall(role.encode() + b' ' + proof(key, role, nonce) + b'\n')
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline:
        answer = line(sock)
        if answer == b'GO':
            return
        if answer != b'WAIT':
            raise ConnectionError('Relay unavailable')
    raise TimeoutError('Board did not connect to relay')


def copy_pair(first, second, stop):
    # Backpressure is bounded to 64 KiB per direction; EOF closes both ends.
    first.setblocking(False)
    second.setblocking(False)
    buffers = {first: bytearray(), second: bytearray()}
    peers = {first: second, second: first}
    last = time.monotonic()
    closing = False
    while not stop.is_set() and time.monotonic() - last < 45:
        readers = [] if closing else [s for s in peers if len(buffers[peers[s]]) < 65536]
        writers = [s for s in peers if buffers[s]]
        if closing and not writers:
            return
        ready, writable, _ = select.select(readers, writers, [], 1)
        for s in ready:
            data = s.recv(min(16384, 65536 - len(buffers[peers[s]])))
            if not data:
                closing = True
                continue
            buffers[peers[s]].extend(data)
            last = time.monotonic()
        for s in writable:
            count = s.send(buffers[s])
            del buffers[s][:count]
            last = time.monotonic()


class Relay:
    def __init__(self, key, host='0.0.0.0', port=5444):
        if len(key) != 64 or any(c not in '0123456789abcdef' for c in key):
            raise ValueError('Relay key must be 32 random bytes in hex')
        self.key = key
        self.stop = threading.Event()
        self.lock = threading.Lock()
        self.slots = threading.BoundedSemaphore(16)
        self.pending = {}
        self.listener = socket.create_server((host, port), backlog=8)
        self.listener.settimeout(1)
        self.port = self.listener.getsockname()[1]

    def serve(self):
        while not self.stop.is_set():
            try:
                peer, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            if not self.slots.acquire(blocking=False):
                peer.close()
                continue
            threading.Thread(target=self.handle, args=(peer,), daemon=True).start()

    def handle(self, peer):
        entry = None
        other = None
        role = None
        try:
            peer.settimeout(4)
            peer.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            nonce = secrets.token_hex(32).encode()
            peer.sendall(nonce + b'\n')
            role_bytes, signature = line(peer).split(b' ')
            role = role_bytes.decode('ascii')
            if role not in ('device', 'controller') or not hmac.compare_digest(signature, proof(self.key, role, nonce)):
                return
            opposite = 'controller' if role == 'device' else 'device'
            entry = {'peer': peer, 'paired': threading.Event(), 'done': threading.Event()}
            with self.lock:
                if role in self.pending:
                    return
                other = self.pending.pop(opposite, None)
                if other is None:
                    self.pending[role] = entry
                else:
                    other['paired'].set()
            if other is not None:
                # The waiting worker owns its socket until it acknowledges pairing.
                if not other['done'].wait(2):
                    return
                remote = other['peer']
                try:
                    remote.sendall(b'GO\n')
                    peer.sendall(b'GO\n')
                    copy_pair(peer, remote, self.stop)
                finally:
                    remote.close()
            else:
                deadline = time.monotonic() + (60 if role == 'device' else 10)
                while not self.stop.is_set() and time.monotonic() < deadline:
                    if entry['paired'].wait(1):
                        entry['done'].set()
                        return
                    peer.sendall(b'WAIT\n')
                with self.lock:
                    # Pairing may race the deadline; hand ownership over if selected.
                    if entry['paired'].is_set():
                        entry['done'].set()
                        return
                    if self.pending.get(role) is entry:
                        del self.pending[role]
        except (OSError, ValueError, UnicodeError):
            pass
        finally:
            with self.lock:
                if self.pending.get(role) is entry:
                    self.pending.pop(role, None)
            if entry is None or not entry['paired'].is_set():
                peer.close()
            elif not entry['done'].is_set():
                # An error during the handoff must wake the other worker.
                peer.close()
                entry['done'].set()
            self.slots.release()

    def close(self):
        self.stop.set()
        self.listener.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, help='Private JSON with key and optional port')
    args = parser.parse_args()
    with open(args.config, encoding='utf-8') as source:
        config = json.load(source)
    relay = Relay(config['key'], port=config.get('port', 5444))
    try:
        print('Mobile relay ready', flush=True)
        relay.serve()
    finally:
        relay.close()


if __name__ == '__main__':
    main()
