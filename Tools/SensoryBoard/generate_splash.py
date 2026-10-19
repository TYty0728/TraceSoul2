"""Regenerate the built-in startup pixels using a locally licensed CJK font."""
from pathlib import Path
import argparse
from PIL import ImageDraw, ImageFont
from bridge import choose_font, slate, rgb565


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--font')
    args = parser.parse_args()
    font = choose_font(args.font)
    image = slate(font)
    draw = ImageDraw.Draw(image)
    draw.ellipse((132, 108, 188, 164), fill='#7de3cb')
    for y, text, size, color in [
        (198, '感官终端已启动', 28, '#f0f5fa'),
        (248, 'USB 接收已就绪', 22, '#7de3cb'),
        (320, '请在电脑控制台发送', 21, '#a8bacd'),
        (356, '文字 · 图片 · 声音', 21, '#a8bacd'),
        (442, 'TraceSoul Terminal 0.1.0', 15, '#91a8bd'),
    ]:
        face = ImageFont.truetype(font, size)
        x = (320 - draw.textlength(text, font=face)) / 2
        draw.text((x, y), text, font=face, fill=color)
    pixels = rgb565(image)
    runs = []
    last, count = None, 0
    for offset in range(0, len(pixels), 2):
        value = int.from_bytes(pixels[offset:offset + 2], 'big')
        if value == last and count < 65535:
            count += 1
        else:
            if count:
                runs.extend((count, last))
            last, count = value, 1
    runs.extend((count, last))
    rows = [', '.join(str(v) for v in runs[i:i + 16]) + ',' for i in range(0, len(runs), 16)]
    target = Path(__file__).parent / 'firmware/main/splash.h'
    target.write_text('// Generated startup pixels; no font file embedded.\n#pragma once\n'
                      '#include <stdint.h>\nstatic const uint16_t SPLASH_RLE[] = {\n' +
                      '\n'.join(rows) + '\n};\n', encoding='utf-8')
    print(f'Generated {len(runs) // 2} runs for {len(pixels) // 2} pixels')


if __name__ == '__main__':
    main()
