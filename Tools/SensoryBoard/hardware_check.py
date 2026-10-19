"""Opt-in hardware checks: replaces the display and optionally plays a short WAV."""
import argparse
import json
from pathlib import Path

from PIL import ImageDraw, ImageFont

import bridge


def run(port, wav=None):
    board = bridge.Board(port)
    report = {}
    try:
        before = board.transact('hello')
        report['before'] = before

        def header(command_id, size, checksum=0):
            board.connection.write(json.dumps(dict(id=command_id, op='audio',
                                                  size=size, checksum=checksum)).encode() + b'\n')

        def expect_error(command_id, expected):
            try:
                board._reply(command_id, 'done', timeout=8)
            except RuntimeError as error:
                if str(error) != '开发板返回：' + expected:
                    raise
                report[expected] = 'rejected'
            else:
                raise AssertionError('Invalid request unexpectedly succeeded')

        header(1001, 3)
        expect_error(1001, 'bad_payload_spec')
        header(1002, 2)
        board._reply(1002, 'ready')
        board.connection.write(b'\x00\x00')
        board._reply(1002, 'chunk', 2)
        expect_error(1002, 'checksum_mismatch')
        header(1003, 4096)
        board._reply(1003, 'ready')
        board.connection.write(b'\x00' * 32)
        expect_error(1003, 'payload_timeout')
        recovered = board.transact('hello')
        assert recovered['frames'] == before['frames']
        assert recovered['plays'] == before['plays']
        assert recovered['uptime_ms'] > before['uptime_ms']
        report['recovered'] = recovered

        font_path = bridge.choose_font()
        card = bridge.slate(font_path, 'TraceSoul · 显示测试')
        draw = ImageDraw.Draw(card)
        for i, color in enumerate(('#ef4444', '#22c55e', '#3b82f6')):
            draw.rectangle((24 + i * 94, 92, 108 + i * 94, 200), fill=color)
        draw.text((24, 240), '图片传输完成\n红 · 绿 · 蓝\n320 × 480',
                  font=ImageFont.truetype(font_path, 26), fill='white', spacing=14)
        report['picture'] = board.transact('frame', bridge.rgb565(card))
        if wav:
            report['audio'] = board.transact('audio', bridge.decode_wav(Path(wav).read_bytes()))
        report['after'] = board.transact('hello')
        assert report['after']['frames'] == before['frames'] + 1
        assert report['after']['plays'] == before['plays'] + bool(wav)
        assert report['after']['uptime_ms'] > recovered['uptime_ms']
        return report
    finally:
        board.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', required=True)
    parser.add_argument('--wav', help='Optional PCM16 WAV, up to 10 seconds')
    parser.add_argument('--report', help='Optional path for receipts (no media contents)')
    args = parser.parse_args()
    result = run(args.port, args.wav)
    output = json.dumps(result, ensure_ascii=False, indent=2)
    if args.report:
        Path(args.report).write_text(output + '\n', encoding='utf-8')
    print(output)
