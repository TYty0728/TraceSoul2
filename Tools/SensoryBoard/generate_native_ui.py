"""Rasterize the terminal's fixed UI labels; no font file is embedded."""
from pathlib import Path
import argparse
from PIL import Image, ImageDraw, ImageFont
from bridge import choose_font

LABELS = {
    'title': 'TraceSoul',
    'home': '感官终端已启动',
    'online': 'Wi-Fi 已连接',
    'connecting': 'Wi-Fi 正在连接…',
    'unconfigured': 'Wi-Fi 尚未配网',
    'network_error': 'Wi-Fi 初始化失败',
    'waiting': '等待电脑端会话',
    'home_hint': '点右上角可打开设置',
    'settings': '终端设置',
    'recent': '控制桥：最近有通信',
    'no_control': '控制桥：等待通信',
    'soul': 'Soul 由电脑端选择',
    'privacy': '暂停麦克风',
    'allow': '允许麦克风',
    'paused': '麦克风已在本机暂停',
    'not_listening': '允许采音不等于正在监听',
    'brightness': '屏幕亮度',
    'level30': '30%', 'level60': '60%', 'level90': '90%',
    'back': '返回主页',
    'setup_hint': '首次配网请使用电脑控制页',
    'start_call': '开始对话', 'end_call': '结束对话',
    'authorise': '请在电脑授权触屏通话',
    'call_hint': '点上方开始对话',
    'starting': '正在准备通话…', 'stopping': '正在结束通话…',
    'call_error': '通话失败，请检查电脑端',
    'listening': '正在听你说话', 'thinking': '正在处理你的话',
    'speaking': '正在回复', 'call_ready': '通话已开始',
    'owner_unconfirmed': '这句未确认是本人', 'speak_more': '请自然多说一点',
    'no_speech': '刚才没有检测到语音', 'try_speaking': '继续听，请再说一句',
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--font')
    parser.add_argument('--output', type=Path, default=Path(__file__).parent / 'firmware/main/native_labels.h')
    args = parser.parse_args()
    font = ImageFont.truetype(choose_font(args.font), 19)
    lines = ['// Generated fixed-label pixels; regenerate with generate_native_ui.py.',
             '#pragma once', '#include <stdint.h>',
             'struct NativeLabel { unsigned width, height; const uint8_t *pixels; };']
    for key, text in LABELS.items():
        bounds = font.getbbox(text)
        width = max(1, int(font.getlength(text) + 1))
        image = Image.new('L', (width, 26))
        ImageDraw.Draw(image).text((0, 2 - bounds[1]), text, font=font, fill=255)
        bits = [1 if value >= 96 else 0 for value in image.get_flattened_data()]
        packed = bytes(sum(bits[i + j] << (7 - j) for j in range(min(8, len(bits) - i)))
                       for i in range(0, len(bits), 8))
        lines.append(f'static const uint8_t label_{key}_pixels[] = {{' + ','.join(map(str, packed)) + '};')
        lines.append(f'static const NativeLabel label_{key} = {{{width},26,label_{key}_pixels}};')
    args.output.write_text('\n'.join(lines) + '\n', encoding='utf-8')
    print(f'Generated {len(LABELS)} fixed labels')


if __name__ == '__main__':
    main()
