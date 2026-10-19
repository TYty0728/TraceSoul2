"""Export computer-only display previews; never instantiate a USB board."""
import argparse
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
from bridge import choose_font
from display import CompanionDisplay, STATES


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--output-directory',type=Path,required=True)
    parser.add_argument('--font')
    args=parser.parse_args()
    output=args.output_directory.resolve();output.mkdir(parents=True,exist_ok=True)
    font_path=choose_font(args.font)
    renderer=CompanionDisplay(font_path)
    examples={
        'standby':'安静待一会儿。\n叫我的名字，就会听你说。',
        'focused':'诶，我在。\n你想跟我说什么？',
        'listening':'今天我们继续测试语音对话。',
        'thinking':'今天我们继续测试语音对话。',
        'speaking':'嗯，我在呢。\n你慢慢说，我听着。',
        'paused':'需要时，在控制台重新开始。',
        'offline':'连接恢复以后，再继续聊天。',
        'error':'暂时没能完成这一步。\n请看看控制台的提示。',
    }
    sheets=[Image.new('RGB',(700,1060),'#e9ede5') for _ in range(2)]
    title_font=ImageFont.truetype(font_path,17)
    for index,state in enumerate(STATES):
        frame, _=renderer.render(dict(state=state,name='小汐',subtitle=examples[state],remaining=21))
        frame.save(output/(state+'.png'))
        sheet=sheets[index//4];position=index%4
        x=20+(position%2)*340;y=45+(position//2)*525
        ImageDraw.Draw(sheet).text((x,y-29),STATES[state][0],font=title_font,fill='#45604d')
        sheet.paste(frame,(x,y))
    for index,sheet in enumerate(sheets):sheet.save(output/f'states-{index+1}.png')
    long_text='这是一段用于检查字幕分页的公开测试文字。字号保持清楚，长句会自动分成多页，所有文字都保留。\n我们先在电脑上看排版，之后再连接开发板验证真实显示。'
    for page in range(len(renderer.pages(long_text))):
        frame,_=renderer.render(dict(state='speaking',name='小汐',subtitle=long_text,page=page))
        frame.save(output/f'caption-page-{page+1}.png')
    print('Exported eight states, two sheets and caption pages. No USB, recording, playback or model requests.')


if __name__=='__main__':
    main()
