import json
import socket
import ssl
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

import bridge
import mobile_relay
import wifi_link
from test_wifi_link import TlsPeer


class MobileChecks(unittest.TestCase):
    def setUp(self):
        self.key = '9a' * 32
        self.relay = mobile_relay.Relay(self.key, '127.0.0.1', 0)
        self.worker = threading.Thread(target=self.relay.serve, daemon=True)
        self.worker.start()

    def tearDown(self):
        self.relay.close()
        self.worker.join(3)

    def device(self, peer):
        def run():
            try:
                with socket.create_connection(('127.0.0.1', self.relay.port), timeout=4) as raw:
                    mobile_relay.register(raw, self.key, 'device')
                    with socket.create_connection(('127.0.0.1', peer.port), timeout=4) as tls_server:
                        mobile_relay.copy_pair(raw, tls_server, self.relay.stop)
            except OSError:
                pass
        thread = threading.Thread(target=run, daemon=True)
        thread.start()
        return thread

    def profile(self, identity):
        return dict(identity, mode='relay', relay_host='127.0.0.1',
                    relay_port=self.relay.port, relay_key=self.key)

    def test_outbound_device_transfers_full_frame_and_reconnects(self):
        identity = wifi_link.identity()
        with tempfile.TemporaryDirectory() as folder:
            for _ in range(2):
                peer = TlsPeer(folder, identity, protocol=True)
                device = self.device(peer)
                profile = self.profile(identity)
                board = bridge.Board('unused')
                board.wifi = type('Settings', (), {'profile': profile, 'status': lambda _: {'mode': 'relay'}})()
                payload = bytes(range(256)) * 1200
                receipt = board.transact('frame', payload)
                self.assertEqual(receipt['status'], 'done')
                self.assertIsNone(board.connection)
                peer.finish()
                device.join(3)
                self.assertEqual(bytes(peer.payload), payload)

    def test_relay_cannot_substitute_unpaired_device(self):
        actual, wrong = wifi_link.identity(), wifi_link.identity()
        with tempfile.TemporaryDirectory() as folder:
            peer = TlsPeer(folder, actual)
            device = self.device(peer)
            with self.assertRaises(ssl.SSLCertVerificationError):
                wifi_link.WifiLink(self.profile(wrong))
            peer.finish()
            device.join(3)
            self.assertEqual(peer.received, [])

    def test_wrong_relay_key_rejected_before_pairing(self):
        with socket.create_connection(('127.0.0.1', self.relay.port), timeout=3) as raw:
            with self.assertRaises(ConnectionError):
                mobile_relay.register(raw, '01' * 32, 'device')
        self.assertFalse(self.relay.pending)

    def test_failed_mobile_switch_preserves_usb_and_private_profile(self):
        with tempfile.TemporaryDirectory() as folder:
            settings = wifi_link.WifiSettings(Path(folder) / 'profile.json')
            settings.profile = dict(self.profile(wifi_link.identity()), mode='usb')
            wifi_link.save_private(settings.path, settings.profile)
            before = settings.path.read_bytes()
            board = bridge.Board('unused')
            with patch.object(wifi_link, 'WifiLink', side_effect=TimeoutError('fixture')):
                with self.assertRaises(TimeoutError):
                    settings.switch(board, 'relay')
            self.assertEqual(settings.path.read_bytes(), before)
            self.assertEqual(settings.status()['mode'], 'usb')
            self.assertNotIn(self.key, json.dumps(settings.status()))

    def test_board_control_token_is_still_required_inside_tls(self):
        identity = wifi_link.identity()
        with tempfile.TemporaryDirectory() as folder:
            peer = TlsPeer(folder, identity)
            device = self.device(peer)
            with self.assertRaises(RuntimeError):
                wifi_link.WifiLink(dict(self.profile(identity), token='0' * 64))
            peer.finish()
            device.join(3)

    def test_role_and_nonce_are_bound_to_proof(self):
        nonce = b'a' * 64
        self.assertNotEqual(mobile_relay.proof(self.key, 'device', nonce),
                            mobile_relay.proof(self.key, 'controller', nonce))
        with socket.create_connection(('127.0.0.1', self.relay.port), timeout=3) as raw:
            mobile_relay.line(raw)
            raw.sendall(b'device ' + mobile_relay.proof(self.key, 'device', nonce) + b'\n')
            self.assertEqual(raw.recv(1), b'')

    def test_disconnect_does_not_replay_or_leave_controller_connected(self):
        device = socket.create_connection(('127.0.0.1', self.relay.port), timeout=3)
        nonce = mobile_relay.line(device)
        device.sendall(b'device ' + mobile_relay.proof(self.key, 'device', nonce) + b'\n')
        with socket.create_connection(('127.0.0.1', self.relay.port), timeout=3) as controller:
            mobile_relay.register(controller, self.key, 'controller')
            while mobile_relay.line(device) != b'GO':
                pass
            device.close()
            self.assertEqual(controller.recv(1), b'')


if __name__ == '__main__':
    unittest.main()
