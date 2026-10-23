"""Keep a local board bridge reachable on a Docker Host's loopback over SSH.

The SSH account needs passwordless sudo for the remote Python helper. Credentials
belong in a private JSON file outside the repository; known SSH keys are required.
Closing this process removes the temporary listener without restarting the Soul.
"""
import argparse
import datetime
import json
import os
import pathlib
import re
import select
import shlex
import socket
import sys
import threading
import time
import urllib.request


def relay(left, right, stop):
    """Forward both directions, preserving responses after a request half-close."""
    peers = {left: right, right: left}
    try:
        while peers and not stop.is_set():
            readable, _, _ = select.select(list(peers), [], [], 0.5)
            for source in readable:
                target = peers[source]
                data = source.recv(65536)
                if data:
                    target.sendall(data)
                else:
                    target.shutdown(socket.SHUT_WR)
                    del peers[source]
    except (OSError, EOFError):
        pass
    finally:
        left.close()
        right.close()


# This helper creates only its listening socket inside the container namespace.
# It returns to the host namespace before opening connections to the SSH forward.
# No packages, credentials, files or configuration are installed in the container.
REMOTE_HELPER = r'''
import ctypes,json,os,signal,socket,subprocess,sys,threading,time
container,port,forward=sys.argv[1],int(sys.argv[2]),int(sys.argv[3])
def inspect():
    x=json.loads(subprocess.check_output(['docker','inspect',container],timeout=10))[0]
    if not x['State']['Running']:raise RuntimeError('container_not_running')
    return x['Id'],x['State']['Pid']
identity,pid=inspect()
listeners=subprocess.check_output(['ss','-H','-ltn','sport = :%s'%forward],text=True,timeout=10).splitlines()
if not listeners or any(line.split()[3]!='127.0.0.1:%s'%forward for line in listeners):
    raise RuntimeError('ssh_forward_not_loopback')
stop=threading.Event()
heartbeat=time.monotonic()
libc=ctypes.CDLL(None,use_errno=True)
def setns(fd):
    if libc.setns(fd,0):raise OSError(ctypes.get_errno(),'setns_failed')
origin=os.open('/proc/self/ns/net',os.O_RDONLY)
target=os.open('/proc/%s/ns/net'%pid,os.O_RDONLY)
listener=socket.socket()
try:
    listener.close()
    setns(target)
    try:
        listener=socket.socket()
        listener.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)
        listener.bind(('127.0.0.1',port))
        listener.listen(16)
        listener.settimeout(1)
    finally:setns(origin)
finally:
    os.close(origin);os.close(target)
def ended():
    global heartbeat
    try:
        while sys.stdin.buffer.readline()==b'ping\n':
            heartbeat=time.monotonic()
            print(json.dumps({'event':'heartbeat'}),flush=True)
    finally:stop.set()
threading.Thread(target=ended,daemon=True).start()
signal.signal(signal.SIGTERM,lambda *_:stop.set())
signal.signal(signal.SIGINT,lambda *_:stop.set())
print(json.dumps({'event':'ready','container_id':identity,'container_pid':pid,
                  'remote_port':port,'bind':'127.0.0.1'}),flush=True)
last=time.monotonic()
try:
    while not stop.is_set():
        if time.monotonic()-heartbeat>25:raise RuntimeError('ssh_heartbeat_expired')
        if time.monotonic()-last>5:
            if inspect()!=(identity,pid):raise RuntimeError('container_changed')
            last=time.monotonic()
        try:client,_=listener.accept()
        except socket.timeout:continue
        try:upstream=socket.create_connection(('127.0.0.1',forward),timeout=10)
        except OSError:client.close();continue
        client.settimeout(30);upstream.settimeout(30)
        threading.Thread(target=relay,args=(client,upstream,stop),daemon=True).start()
finally:
    stop.set();listener.close()
'''


def remote_source():
    # Keep the exact same byte relay on both sides of the encrypted connection.
    import inspect
    return 'import select\n' + inspect.getsource(relay) + '\n' + REMOTE_HELPER


def connection_settings(path):
    source = pathlib.Path(path)
    if source.stat().st_size > 65536:
        raise ValueError('SSH connection file is too large')
    value = json.loads(source.read_text(encoding='utf-8-sig'))
    if not isinstance(value, dict):
        raise ValueError('SSH connection file must be an object')
    for field in ('host', 'username'):
        if not isinstance(value.get(field), str) or not re.fullmatch(r'[A-Za-z0-9_.:-]{1,253}', value[field]):
            raise ValueError('Invalid SSH ' + field)
    port = value.get('port', 22)
    if type(port) is not int or not 1 <= port <= 65535:
        raise ValueError('Invalid SSH port')
    if bool(value.get('password')) == bool(value.get('private_key_path')):
        raise ValueError('Specify exactly one SSH password or private_key_path')
    if value.get('password') and not isinstance(value['password'], str):
        raise ValueError('Invalid SSH password type')
    if value.get('private_key_path') and not isinstance(value['private_key_path'], str):
        raise ValueError('Invalid SSH private key path')
    return value


def check_local_bridge(port):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(f'http://127.0.0.1:{port}/api/health', timeout=5) as response:
        health = json.loads(response.read(4096))
    if health.get('service') != 'tracesoul-sensory-usb' or health.get('preview_only') is not False:
        raise ValueError('Local port is not a hardware board bridge')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--connection-config', required=True)
    parser.add_argument('--known-hosts', default=str(pathlib.Path.home()/'.ssh/known_hosts'))
    parser.add_argument('--container', default='tracesoul2')
    parser.add_argument('--local-port', type=int, default=5089)
    parser.add_argument('--remote-port', type=int, default=5089)
    parser.add_argument('--state-file', help='Optional non-secret runtime status JSON')
    parser.add_argument('--stop-file', help='Exit gracefully when this file appears')
    args = parser.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}', args.container):
        parser.error('Invalid Docker container name')
    if not all(1024 <= p <= 65535 for p in (args.local_port, args.remote_port)):
        parser.error('Ports must be between 1024 and 65535')
    if args.local_port != args.remote_port:
        parser.error('Local and remote ports must match the HTTP bridge Host validation')
    if args.stop_file and pathlib.Path(args.stop_file).exists():
        parser.error('Remove the previous stop file before starting')
    settings = connection_settings(args.connection_config)
    check_local_bridge(args.local_port)
    import paramiko
    client = paramiko.SSHClient()
    client.load_host_keys(args.known_hosts)
    client.set_missing_host_key_policy(paramiko.RejectPolicy())
    stop = threading.Event()
    command_input = None
    state = {'event': 'starting', 'pid': os.getpid(), 'local_port': args.local_port, 'remote_port': args.remote_port}

    def save_state():
        state['updated_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if args.state_file:
            target = pathlib.Path(args.state_file)
            temporary = target.with_suffix(target.suffix+'.tmp')
            temporary.write_text(json.dumps(state, indent=2), encoding='utf-8')
            temporary.replace(target)

    def report(update):
        state.update(update)
        save_state()
        print(json.dumps(state), flush=True)

    def accept(channel, _origin, _server):
        def work():
            try:
                local = socket.create_connection(('127.0.0.1', args.local_port), timeout=10)
                local.settimeout(30)
                relay(channel, local, stop)
            except OSError:
                channel.close()
        threading.Thread(target=work, daemon=True).start()

    try:
        client.connect(settings['host'], port=settings.get('port', 22), username=settings['username'],
                       password=settings.get('password'), key_filename=settings.get('private_key_path'),
                       look_for_keys=False, allow_agent=False, timeout=15, auth_timeout=15, banner_timeout=15)
        transport = client.get_transport()
        transport.set_keepalive(15)
        forward = transport.request_port_forward('127.0.0.1', 0, handler=accept)
        command = 'sudo -n python3 -u -c ' + shlex.quote(remote_source()) + ' ' + ' '.join(
            shlex.quote(str(v)) for v in (args.container, args.remote_port, forward))
        command_input, output, error = client.exec_command(command, timeout=25)
        first = output.readline(4096)
        if not first:
            raise RuntimeError('Remote loopback listener could not start; check sudo, container and port availability')
        ready = json.loads(first)
        if ready.get('event') != 'ready' or ready.get('bind') != '127.0.0.1':
            raise RuntimeError('Unexpected remote tunnel acknowledgement')
        report(dict(ready, event='ready'))
        output.channel.settimeout(5)
        last_ping = 0
        last_reply = time.monotonic()
        pending = b''
        while transport.is_active():
            if args.stop_file and pathlib.Path(args.stop_file).exists():
                return
            if output.channel.exit_status_ready():
                raise RuntimeError('Remote tunnel helper stopped')
            if output.channel.recv_ready():
                pending += output.channel.recv(4096)
                if len(pending) > 8192:
                    raise RuntimeError('Invalid tunnel heartbeat')
                while b'\n' in pending:
                    line, pending = pending.split(b'\n', 1)
                    if json.loads(line).get('event') != 'heartbeat':
                        raise RuntimeError('Unexpected tunnel heartbeat')
                    last_reply = time.monotonic()
                    save_state()
            if time.monotonic() - last_reply > 20:
                raise RuntimeError('SSH heartbeat expired')
            if time.monotonic() - last_ping >= 5:
                output.channel.sendall(b'ping\n')
                last_ping = time.monotonic()
            stop.wait(1)
        raise RuntimeError('SSH connection closed')
    finally:
        stop.set()
        try:
            if command_input is not None:
                # EOF is the helper's shutdown instruction. Wait for socket cleanup.
                try:
                    command_input.channel.shutdown_write()
                    for _ in range(30):
                        if command_input.channel.exit_status_ready():
                            break
                        time.sleep(0.1)
                except (OSError, EOFError):
                    pass
        finally:
            client.close()
            report({'event': 'stopped'})


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        pass
    except Exception as error:
        # Never render library exceptions that might contain connection details.
        print('Tunnel stopped: '+type(error).__name__+'. Check private SSH settings, known host key and remote listener.', file=sys.stderr)
        raise SystemExit(1)
