"""Pair a local TraceSoul client with the private TTS key without printing credentials."""
import argparse
import json
from pathlib import Path
from access import load_key,write_private_json


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--runtime',type=Path,required=True)
    parser.add_argument('--client-config',type=Path,required=True)
    args=parser.parse_args()
    root=args.runtime.resolve(strict=True)
    key=load_key(root)
    url='http://127.0.0.1:5088/v1/audio/speech'
    if args.client_config.exists():
        existing=json.loads(args.client_config.read_text(encoding='utf-8'))
        if existing.get('api_key')!=key or existing.get('api_url')!=url:
            raise RuntimeError('Existing client configuration differs; it was not overwritten.')
    write_private_json(args.client_config,{'api_url':url,'api_key':key})
    print('Private TTS client configuration is ready. No credentials printed.')


if __name__=='__main__':
    main()
