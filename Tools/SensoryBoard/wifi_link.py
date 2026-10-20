"""USB provisioning and certificate-verified TLS transport. No plaintext Wi-Fi fallback."""
import csv
import datetime
import io
import ipaddress
import json
import os
from pathlib import Path
import secrets
import socket
import ssl
import subprocess
import time


def save_private(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.' + secrets.token_hex(8) + '.tmp')
    try:
        with temporary.open('xb') as output:
            if os.name == 'nt':
                row = next(csv.reader(io.StringIO(subprocess.check_output(
                    ['whoami', '/user', '/fo', 'csv', '/nh'], text=True, creationflags=subprocess.CREATE_NO_WINDOW))))
                subprocess.run(['icacls', str(temporary), '/inheritance:r', '/grant:r',
                    '*' + row[1] + ':(F)', '*S-1-5-18:(F)'], check=True,
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
            else:
                os.chmod(temporary, 0o600)
            output.write(json.dumps(value, ensure_ascii=False).encode())
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def identity():
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID
    key = ec.generate_private_key(ec.SECP256R1())
    name = secrets.token_hex(12) + '.tracesoul.invalid'
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, name)])
    now = datetime.datetime.now(datetime.timezone.utc)
    certificate = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(days=1))
        .not_valid_after(now + datetime.timedelta(days=3650))
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(x509.SubjectAlternativeName([x509.DNSName(name)]), critical=False)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
        .sign(key, hashes.SHA256()))
    return dict(version=1, mode='usb', host='', server_name=name, token=secrets.token_hex(32),
        certificate=certificate.public_bytes(serialization.Encoding.PEM).decode(),
        private_key=key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                      serialization.NoEncryption()).decode())


class WifiLink:
    def __init__(self, profile, port=5443):
        address = ipaddress.ip_address(profile['host'])
        if address.version != 4 or not address.is_private or address.is_multicast or address.is_unspecified:
            raise ValueError('开发板地址必须为本地局域网IPv4地址')
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.load_verify_locations(cadata=profile['certificate'])
        raw = socket.create_connection((str(address), port), timeout=8)
        try:
            raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            self.connection = context.wrap_socket(raw, server_hostname=profile['server_name'])
            self.connection.settimeout(0.2)
            self.write(json.dumps(dict(token=profile['token'])).encode() + b'\n')
            if json.loads(self.read_until(b'\n', 256)).get('authenticated') is not True:
                raise RuntimeError('开发板鉴权失败')
        except Exception:
            raw.close()
            if hasattr(self, 'connection'):
                self.connection.close()
            raise

    def write(self, data):
        self.connection.settimeout(5)
        try:
            self.connection.sendall(data)
            return len(data)
        finally:
            self.connection.settimeout(0.2)

    def read(self, size):
        try:
            data = self.connection.recv(size)
            if not data:
                raise ConnectionError('开发板无线连接已断开；结果未知，不自动重播')
            return data
        except socket.timeout:
            return b''

    def read_until(self, expected=b'\n', size=1024):
        output = bytearray()
        deadline = time.monotonic() + 0.25
        while len(output) < size:
            block = self.read(1)
            if not block:
                if not output and time.monotonic() >= deadline:
                    break
                if time.monotonic() >= deadline + 2:
                    raise TimeoutError('无线回执不完整，未自动重试')
                continue
            output.extend(block)
            if output.endswith(expected):
                break
        return bytes(output)

    def close(self):
        self.connection.close()


class WifiSettings:
    def __init__(self, path):
        self.path = Path(path)
        self.profile = json.loads(self.path.read_text(encoding='utf-8')) if self.path.is_file() else None

    def status(self):
        return dict(mode=(self.profile or {}).get('mode', 'usb'),
                    paired=self.profile is not None, host=(self.profile or {}).get('host', ''))

    def setup(self, board, ssid, password):
        if not isinstance(ssid, str) or not 1 <= len(ssid.encode()) <= 32 or '\0' in ssid:
            raise ValueError('Wi-Fi名称应为1～32个UTF-8字节')
        if not isinstance(password, str) or not 8 <= len(password.encode()) <= 63 or '\0' in password:
            raise ValueError('Wi-Fi密码应为8～63个UTF-8字节；需要WPA2/WPA3网络')
        with board.lock:
            if self.profile and self.profile.get('mode') != 'usb':
                raise ValueError('请先切回USB，再通过数据线配网')
            if not str(board.transact('hello').get('firmware','')).startswith('0.4.'):
                raise ValueError('需要先更新开发板Wi-Fi固件0.4.x')
            if self.profile is None:
                self.profile = identity()
                save_private(self.path, self.profile)
            payload = {key: self.profile[key] for key in ('token', 'certificate', 'private_key')}
            payload.update(ssid=ssid, password=password)
            board.transact('wifi_setup', json.dumps(payload, ensure_ascii=False).encode())
            board.transact('reboot')
            board.close()
            return dict(saved=True, message='已保存到开发板并重启。稍候点击检查Wi-Fi状态。')

    def switch(self, board, mode):
        if mode not in ('wifi', 'usb'):
            raise ValueError('未知连接方式')
        with board.lock:
            if mode == 'usb':
                if self.profile:
                    self.profile['mode'] = mode
                    save_private(self.path, self.profile)
                board.close()
                return self.status()
            if not self.profile:
                raise ValueError('请先通过USB配网')
            if self.profile.get('mode') == 'usb':
                state = board.transact('hello')
                if not state.get('wifi_connected'):
                    raise ValueError('开发板尚未连上Wi-Fi；请稍候，或核对2.4GHz网络和密码')
                candidate = dict(self.profile, host=state['wifi_ip'], mode='wifi')
            else:
                candidate = dict(self.profile)
            # Authenticate the new path before committing a transport change.
            board.close()
            link = WifiLink(candidate)
            link.close()
            self.profile = candidate
            save_private(self.path, self.profile)
            board.close()
            return self.status()
