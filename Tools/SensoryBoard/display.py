"""320x480 companion frames shared by computer preview and explicit board output."""
import base64
import io
from PIL import Image, ImageDraw, ImageFont, ImageOps

WIDTH, HEIGHT, SCALE = 320, 480, 3
STATES = {
    'standby': ('发呆中', '叫我的名字，就会听你说', '#78978a'),
    'focused': ('我在呢', '注意力集中', '#659884'),
    'listening': ('正在听你说', '说完后，停顿一下就好', '#518f89'),
    'thinking': ('让我想一想', '正在整理这句话', '#9d8b65'),
    'speaking': ('说给你听', '字幕随回复显示', '#688caa'),
    'paused': ('休息一下', '监听已暂停', '#8b929a'),
    'offline': ('连接断开', '等待重新连接', '#9b8e88'),
    'error': ('遇到一点问题', '请在控制台查看详情', '#b28179'),
}


class CompanionDisplay:
    def __init__(self, font_path):
        self.font_path = font_path

    def font(self, size):
        return ImageFont.truetype(self.font_path, size * SCALE)

    def pages(self, subtitle):
        """Fixed legible type; paginate instead of shrinking, omitting or overflowing."""
        if not isinstance(subtitle, str) or len(subtitle) > 500:
            raise ValueError('字幕应为0～500字')
        subtitle = subtitle.replace('\r\n', '\n').replace('\r', '\n')
        draw = ImageDraw.Draw(Image.new('RGB', (1, 1)))
        font = self.font(20)
        lines, line = [], ''
        for char in subtitle:
            if char == '\n':
                lines.append(line); line = ''
                continue
            if draw.textlength(line + char, font=font) > 252 * SCALE:
                # Do not start a line with common closing punctuation when moving one
                # preceding character keeps the punctuation attached and within bounds.
                if char in '，。！？；：、）》】”’.,!?;:' and len(line) > 1:
                    lines.append(line[:-1]); line = line[-1] + char
                else:
                    lines.append(line); line = char
            else:
                line += char
        lines.append(line)
        return [lines[start:start+4] for start in range(0, len(lines), 4)]

    def render(self, request):
        if not isinstance(request, dict):
            raise ValueError('显示请求应为对象')
        state = request.get('state', 'standby')
        name = request.get('name', '小汐')
        subtitle = request.get('subtitle', '')
        page = request.get('page', 0)
        remaining = request.get('remaining')
        if not isinstance(state, str) or state not in STATES:
            raise ValueError('未知的显示状态')
        if not isinstance(name, str) or not name.strip() or len(name) > 32 or any(ord(c)<32 for c in name):
            raise ValueError('角色名字应为1～32字，不能换行')
        if remaining is not None and (type(remaining) is not int or not 0 <= remaining <= 30):
            raise ValueError('注意力剩余秒数应为0～30')
        pages = self.pages(subtitle)
        caption_preview = request.get('caption_preview', False)
        if type(caption_preview) is not bool:
            raise ValueError('字幕预览标志应为布尔值')
        truncated = caption_preview and len(pages)>1
        if truncated:
            pages = [pages[0][:3] + [pages[0][3][:-1]+'…']]
        if type(page) is not int or not 0 <= page < len(pages):
            raise ValueError('字幕页码超出范围')
        image = Image.new('RGB', (WIDTH*SCALE, HEIGHT*SCALE), '#f7f7f2')
        draw = ImageDraw.Draw(image)
        xy = lambda coordinates: tuple(round(v*SCALE) for v in coordinates)
        def rect(points, radius, fill, outline=None, width=1):
            draw.rounded_rectangle(xy(points), radius=radius*SCALE, fill=fill, outline=outline, width=width*SCALE)
        def ellipse(points, fill, outline=None, width=1):
            draw.ellipse(xy(points), fill=fill, outline=outline, width=width*SCALE)
        def line(points, fill, width=2):
            draw.line(xy(points), fill=fill, width=width*SCALE)
        def arc(points, start, end, fill, width=3):
            draw.arc(xy(points), start, end, fill=fill, width=width*SCALE)
        def text(x, y, value, size, color, anchor='lt'):
            draw.text(xy((x,y)), value, font=self.font(size), fill=color, anchor=anchor)
        def fit(value, width, size):
            while value and draw.textlength(value, font=self.font(size)) > width*SCALE:
                value = value[:-2] + '…' if len(value)>1 else ''
            return value

        title, hint, accent = STATES[state]
        ink, muted = '#293d3c', '#7b8882'
        text(18,18,fit(name.strip(),140,23),23,ink)
        text(19,46,'陪你说说话',10,muted)
        artwork = request.get('image')
        if artwork is not None:
            if not isinstance(artwork,str) or len(artwork)>6*1024*1024:
                raise ValueError('表情图片应为最多4MB的Base64图片')
            try:
                raw=base64.b64decode(artwork,validate=True)
                if len(raw)>4*1024*1024: raise ValueError()
                with Image.open(io.BytesIO(raw)) as source:
                    if source.width*source.height>16_000_000 or source.format not in ('PNG','JPEG','WEBP','GIF'): raise ValueError()
                    # Static GIF first frame only; QQ keeps the original animation.
                    source.seek(0)
                    fitted=ImageOps.contain(ImageOps.exif_transpose(source).convert('RGBA'),(284*SCALE,168*SCALE))
                    image.paste(fitted,((WIDTH*SCALE-fitted.width)//2,70*SCALE+(168*SCALE-fitted.height)//2),fitted)
            except (OSError,ValueError,Image.DecompressionBombError):
                raise ValueError('表情图片无效或尺寸过大') from None
        else:
            # Do not disguise an absent shared-library image as a different character.
            text(160,155,'暂无可用表情',16,muted,'mm')
        text(160,260,title,23,ink,'mm')
        # A rendered frame cannot tick by itself. Only draw seconds when the
        # caller supplies an actual snapshot; never invent a frozen 30s timer.
        if state == 'focused' and remaining is not None:
            hint = f'注意力还有 {remaining} 秒'
            rect((93,280,227,283),1,'#e1e7dc')
            if remaining:
                rect((93,280,93+134*remaining/30,283),1,accent)
        text(160,299 if state=='focused' else 288,hint,12,muted,'mm')

        rect((16,317,304,456),16,'#ffffff', '#e7ebe2')
        label = '听到的话' if state == 'listening' else '这次想说'
        text(32,330,label,11,muted)
        if subtitle:
            for index, caption_line in enumerate(pages[page]):
                text(32,350+index*25,caption_line,20,ink)
        else:
            text(32,369,'这里会显示听写或回复',17,'#9ba69d')
        text(20,465,'点字幕左右两侧翻页' if len(pages)>1 else 'TraceSoul · 陪伴终端',10,'#9ba69d')
        text(300,465,'完整回复见网页' if truncated else f'{page+1} / {len(pages)}',10,'#9ba69d','rt')
        return image.resize((WIDTH,HEIGHT),Image.Resampling.LANCZOS), {
            'state':state, 'page':page, 'page_count':len(pages), 'width':WIDTH, 'height':HEIGHT,
            'font_size':20, 'caption_lines':pages[page], 'preview_only':True, 'artwork':artwork is not None,
            'caption_preview':truncated,
        }
