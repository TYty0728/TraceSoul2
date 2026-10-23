import importlib.util
import pathlib
import socket
import subprocess
import sys
import threading
import unittest

spec = importlib.util.spec_from_file_location('remote_tunnel', pathlib.Path(__file__).with_name('remote_tunnel.py'))
tunnel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tunnel)


class RemoteTunnelChecks(unittest.TestCase):
    def test_different_ports_rejected_before_credentials_or_network_access(self):
        result = subprocess.run([sys.executable, str(pathlib.Path(__file__).with_name('remote_tunnel.py')),
                                 '--connection-config', 'missing-private-credentials.json',
                                 '--local-port', '5089', '--remote-port', '15089'],
                                capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 2)
        self.assertIn('ports must match', result.stderr)

    def test_half_closed_request_still_receives_full_response(self):
        caller, incoming = socket.socketpair()
        outgoing, server = socket.socketpair()
        for stream in (caller, incoming, outgoing, server):
            stream.settimeout(2)
        stopped = threading.Event()
        worker = threading.Thread(target=tunnel.relay, args=(incoming, outgoing, stopped))
        worker.start()
        try:
            caller.sendall(b'request')
            caller.shutdown(socket.SHUT_WR)
            self.assertEqual(server.recv(100), b'request')
            self.assertEqual(server.recv(1), b'')
            server.sendall(b'complete response')
            server.shutdown(socket.SHUT_WR)
            self.assertEqual(caller.recv(100), b'complete response')
            self.assertEqual(caller.recv(1), b'')
        finally:
            stopped.set()
            caller.close()
            server.close()
            worker.join(3)
        self.assertFalse(worker.is_alive())

    def test_stop_releases_idle_connections(self):
        caller, incoming = socket.socketpair()
        outgoing, server = socket.socketpair()
        stopped = threading.Event()
        worker = threading.Thread(target=tunnel.relay, args=(incoming, outgoing, stopped))
        worker.start()
        stopped.set()
        worker.join(2)
        self.assertFalse(worker.is_alive())
        caller.settimeout(1)
        server.settimeout(1)
        try:
            self.assertEqual(caller.recv(1), b'')
            self.assertEqual(server.recv(1), b'')
        finally:
            caller.close()
            server.close()


if __name__ == '__main__':
    unittest.main()
