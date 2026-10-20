import json
from pathlib import Path
import socket
import ssl
import tempfile
import threading
import unittest
from unittest.mock import patch

import bridge
import wifi_link


class TlsPeer:
    def __init__(self, folder, profile, protocol=False):
        cert = Path(folder) / 'certificate.pem'
        key = Path(folder) / 'key.pem'
        cert.write_text(profile['certificate'])
        key.write_text(profile['private_key'])
        self.context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.context.load_cert_chain(cert, key)
        self.listener = socket.socket()
        self.listener.bind(('127.0.0.1', 0))
        self.port = self.listener.getsockname()[1]
        self.listener.listen(1)
        self.listener.settimeout(5)
        self.received = []
        self.payload = bytearray()
        self.error = None
        def serve():
            try:
                raw, _ = self.listener.accept()
                with self.context.wrap_socket(raw, server_side=True) as stream:
                    stream.settimeout(3)
                    with stream.makefile('rwb', buffering=0) as channel:
                        auth = json.loads(channel.readline())
                        self.received.append(auth)
                        accepted = auth.get('token') == profile['token']
                        channel.write(json.dumps(dict(authenticated=accepted)).encode() + b'\n')
                        if accepted and protocol:
                            header = json.loads(channel.readline())
                            self.received.append(header)
                            if header.get('op')=='control':
                                channel.write(json.dumps(dict(id=header['id'],protocol=1,status='ready',
                                    controller=header['controller'],boot='b'*32,sequence=header['ack'],action='',
                                    local_mic_paused=True)).encode()+b'\n')
                                return
                            def reply(status, received=0):
                                channel.write(json.dumps(dict(id=header['id'], protocol=1, status=status,
                                                              received=received)).encode() + b'\n')
                            reply('ready')
                            while len(self.payload) < header['size']:
                                block = channel.read(min(4096, header['size'] - len(self.payload)))
                                if not block:
                                    raise RuntimeError('Payload interrupted')
                                self.payload.extend(block)
                                reply('chunk', len(self.payload))
                            reply('done', len(self.payload))
            except (ssl.SSLError, ConnectionError):
                pass
            except Exception as error:
                self.error = error
            finally:
                self.listener.close()
        self.thread = threading.Thread(target=serve, daemon=True)
        self.thread.start()

    def finish(self):
        self.thread.join(6)
        if self.thread.is_alive():
            raise AssertionError('TLS fixture did not exit')
        if self.error:
            raise self.error


class WifiChecks(unittest.TestCase):
    def test_device_control_uses_verified_tls_and_request_receipt(self):
        profile=dict(wifi_link.identity(),mode='wifi',host='127.0.0.1')
        with tempfile.TemporaryDirectory() as folder:
            peer=TlsPeer(folder,profile,protocol=True)
            board=bridge.Board('unused')
            board.wifi=type('Settings',(),{'profile':profile,'status':lambda _:{'mode':'wifi'}})()
            factory=wifi_link.WifiLink
            with patch.object(wifi_link,'WifiLink',side_effect=lambda p:factory(p,port=peer.port)):
                result=board.device_control(dict(controller='a'*32,ack=9,phase='paused',active=False,allowed=True,error=None))
            peer.finish()
            self.assertEqual(result['sequence'],9)
            self.assertTrue(result['local_mic_paused'])
            self.assertIsNone(board.connection,'each completed control command closes its TLS session')
            self.assertEqual(peer.received[1]['controller'],'a'*32)
            self.assertNotIn('token',peer.received[1])

    def test_invalid_device_control_never_connects_or_writes(self):
        board=bridge.Board('unused')
        request=dict(controller='a'*32,ack=0,phase='paused',active=False,allowed=True,error=None)
        for field,value in [('controller','wrong'),('ack',True),('ack',-1),('phase','fake'),('allowed','yes'),('error','private upstream body')]:
            with patch.object(board,'_connect') as connect:
                with self.assertRaises(ValueError):board.device_control(dict(request,**{field:value}))
                connect.assert_not_called()

    def test_verified_tls_reuses_real_chunk_and_receipt_protocol(self):
        profile = dict(wifi_link.identity(), mode='wifi', host='127.0.0.1')
        with tempfile.TemporaryDirectory() as folder:
            peer = TlsPeer(folder, profile, protocol=True)
            board = bridge.Board('unused')
            board.wifi = type('Settings', (), {'profile': profile, 'status': lambda _: {'mode':'wifi'}})()
            factory = wifi_link.WifiLink
            with patch.object(wifi_link, 'WifiLink', side_effect=lambda p: factory(p, port=peer.port)):
                payload = bytes(range(256)) * 1200
                result = board.transact('frame', payload)
            board.close()
            peer.finish()
            self.assertEqual(result['status'], 'done')
            self.assertEqual(bytes(peer.payload), payload)

    def test_unpaired_certificate_rejected_before_token_or_output(self):
        actual = dict(wifi_link.identity(), host='127.0.0.1')
        wrong = dict(wifi_link.identity(), host='127.0.0.1')
        with tempfile.TemporaryDirectory() as folder:
            peer = TlsPeer(folder, actual)
            with self.assertRaises(ssl.SSLCertVerificationError):
                wifi_link.WifiLink(wrong, port=peer.port)
            peer.finish()
            self.assertEqual(peer.received, [])

    def test_wrong_token_cannot_open_control_channel(self):
        actual = dict(wifi_link.identity(), host='127.0.0.1')
        with tempfile.TemporaryDirectory() as folder:
            peer = TlsPeer(folder, actual)
            with self.assertRaises(RuntimeError):
                wifi_link.WifiLink(dict(actual, token='0' * 64), port=peer.port)
            peer.finish()

    def test_bad_wifi_input_has_no_pairing_or_device_side_effect(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'wifi-client.json'
            settings = wifi_link.WifiSettings(path)
            for ssid, password in [('x', 'short'), ('字' * 11, '12345678'), ('x\0x', '12345678')]:
                with self.assertRaises(ValueError):
                    settings.setup(None, ssid, password)
            self.assertFalse(path.exists())

    def test_usb_provisioning_never_stores_wifi_password_on_computer(self):
        class Device:
            lock = threading.RLock()
            def __init__(self): self.commands = []
            def transact(self, op, payload=b''):
                self.commands.append((op, payload))
                return dict(firmware='0.4.0')
            def close(self): pass
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'wifi-client.json'
            settings = wifi_link.WifiSettings(path)
            device = Device()
            settings.setup(device, 'fixture-network', 'fixture-password')
            value = path.read_text()
            self.assertNotIn('fixture-password', value)
            self.assertNotIn('fixture-network', value)
            payload = json.loads(device.commands[1][1])
            self.assertEqual(payload['password'], 'fixture-password')
            self.assertEqual(device.commands[-1][0], 'reboot')
            public = json.dumps(settings.status())
            self.assertNotIn(settings.profile['token'], public)
            self.assertNotIn('PRIVATE KEY', public)

    def test_failed_wireless_switch_keeps_usb_mode_and_profile(self):
        class Device:
            lock = threading.RLock()
            def transact(self, op): return dict(wifi_connected=True, wifi_ip='192.168.1.2')
            def close(self): pass
        with tempfile.TemporaryDirectory() as folder:
            settings = wifi_link.WifiSettings(Path(folder) / 'wifi-client.json')
            settings.profile = wifi_link.identity()
            wifi_link.save_private(settings.path, settings.profile)
            before = settings.path.read_bytes()
            with patch.object(wifi_link, 'WifiLink', side_effect=ssl.SSLError('fixture rejection')):
                with self.assertRaises(ssl.SSLError):
                    settings.switch(Device(), 'wifi')
            self.assertEqual(settings.status()['mode'], 'usb')
            self.assertEqual(settings.path.read_bytes(), before)


if __name__ == '__main__':
    unittest.main()
