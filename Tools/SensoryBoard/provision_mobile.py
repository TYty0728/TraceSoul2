"""USB-only mobile relay provisioning; never accepts secrets on the command line."""
import argparse
import json
import re
from pathlib import Path

from bridge import Board
from wifi_link import WifiSettings, save_private


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--connection-config', required=True)
    parser.add_argument('--relay-config', required=True)
    parser.add_argument('--port', default='COM3')
    args = parser.parse_args()
    relay = json.loads(Path(args.relay_config).read_text(encoding='utf-8-sig'))
    if not re.fullmatch(r'[A-Za-z0-9.-]{1,253}', relay.get('host', '')) or not re.fullmatch(r'[0-9a-f]{64}', relay.get('key', '')):
        raise ValueError('Invalid relay settings')
    if type(relay.get('port')) is not int or not 1024 <= relay['port'] <= 65535:
        raise ValueError('Invalid relay port')
    settings = WifiSettings(args.connection_config)
    if not settings.profile:
        raise ValueError('Pair the board over USB first')
    board = Board(args.port)
    try:
        state = board.transact('hello')
        if state.get('firmware') != '0.4.9':
            raise ValueError('This provisioner requires firmware 0.4.9')
        # This private backup includes the identity, never the Wi-Fi password.
        backup = settings.path.with_name(settings.path.name + '.before-mobile.json')
        if not backup.exists():
            save_private(backup, settings.profile)
        board.transact('relay_setup', json.dumps({k: relay[k] for k in ('host', 'key', 'port')}).encode())
        candidate = dict(settings.profile, mode='usb', relay_host=relay['host'], relay_key=relay['key'], relay_port=relay['port'])
        save_private(settings.path, candidate)
        board.transact('reboot')
    finally:
        board.close()
    print('Mobile settings saved. Start the panel and explicitly test relay mode.')


if __name__ == '__main__':
    main()
