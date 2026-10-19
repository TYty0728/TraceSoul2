"""Local-only USB output bridge for the Waveshare sensory terminal."""
import argparse
import base64
import io
import hashlib
import json
import math
import os
import re
from pathlib import Path
import secrets
import struct
import subprocess
import tempfile
import threading
import time
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from PIL import Image, ImageDraw, ImageFont, ImageOps
from display import CompanionDisplay
import serial

WIDTH, HEIGHT = 320, 480
CHUNK = 4096
MAX_BODY = 6 * 1024 * 1024
Image.MAX_IMAGE_PIXELS = 16_000_000


def checksum(data):
    result = 2166136261
    for value in data:
        result = ((result ^ value) * 16777619) & 0xFFFFFFFF
    return result


def rgb565(image):
    image = image.convert('RGB')
    if image.size != (WIDTH, HEIGHT):
        raise ValueError('画面尺寸必须是 320×480')
    output = bytearray(WIDTH * HEIGHT * 2)
    for i, (r, g, b) in enumerate(image.get_flattened_data()):
        value = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)
        output[i * 2:i * 2 + 2] = value.to_bytes(2, 'big')
    return bytes(output)


def choose_font(path=None):
    paths = [path] if path else [
        str(Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Fonts/msyh.ttc'),
        '/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc',
    ]
    for candidate in paths:
        if candidate and Path(candidate).is_file():
            return candidate
    raise ValueError('找不到中文字体，请用 --font 指定本机字体文件')


def slate(font_path, title='TraceSoul · 感官终端'):
    image = Image.new('RGB', (WIDTH, HEIGHT), '#101c2c')
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((16, 16, 304, 60), radius=12, fill='#1c3446')
    draw.text((28, 26), title, font=ImageFont.truetype(font_path, 20), fill='#7de3cb')
    return image


def text_frame(text, font_path):
    if not isinstance(text, str) or not text.strip() or len(text) > 500:
        raise ValueError('请输入 1～500 字的文字')
    image = slate(font_path)
    draw = ImageDraw.Draw(image)
    for size in (28, 24, 20, 16):
        font = ImageFont.truetype(font_path, size)
        lines, line = [], ''
        for char in text:
            if char == '\n':
                lines.append(line); line = ''
            elif draw.textlength(line + char, font=font) > 272:
                lines.append(line); line = char
            else:
                line += char
        lines.append(line)
        if len(lines) * (size + 10) <= 348:
            break
    else:
        raise ValueError('文字超过一屏，请缩短后发送')
    for i, line in enumerate(lines):
        draw.text((24, 84 + i * (size + 10)), line, font=font, fill='#f0f5fa')
    draw.text((24, 446), 'USB 已连接 · 内容来自电脑',
              font=ImageFont.truetype(font_path, 15), fill='#91a8bd')
    return image


def picture_frame(data, font_path):
    with Image.open(io.BytesIO(data)) as source:
        if source.width * source.height > 16_000_000:
            raise ValueError('图片最多 1600 万像素')
        source = ImageOps.exif_transpose(source).convert('RGB')
        fitted = ImageOps.contain(source, (288, 384))
    image = slate(font_path, 'TraceSoul · 图片')
    image.paste(fitted, ((WIDTH - fitted.width) // 2, 76 + (384 - fitted.height) // 2))
    return image


def decode_wav(data, max_seconds=10):
    with wave.open(io.BytesIO(data), 'rb') as wav:
        channels, rate, frames = wav.getnchannels(), wav.getframerate(), wav.getnframes()
        if (wav.getsampwidth() != 2 or channels not in (1, 2) or
                not 8000 <= rate <= 96000 or not 0 < frames <= rate * max_seconds):
            raise ValueError(f'请上传不超过 {max_seconds} 秒、16 位 PCM、单/双声道 WAV')
        raw = wav.readframes(frames)
    if len(raw) != frames * channels * 2:
        raise ValueError('WAV 数据不完整')
    values = struct.unpack('<' + 'h' * (frames * channels), raw)
    mono = values if channels == 1 else [(values[i] + values[i + 1]) // 2 for i in range(0, len(values), 2)]
    count = round(frames * 48000 / rate)
    output = bytearray(count * 2)
    for i in range(count):
        pos = i * rate / 48000
        left = min(int(pos), frames - 1)
        right = min(left + 1, frames - 1)
        sample = round(mono[left] + (mono[right] - mono[left]) * (pos - left))
        struct.pack_into('<h', output, i * 2, sample)
    return bytes(output)


def chime():
    samples = bytearray()
    for i in range(24000):
        amplitude = 3500 * math.sin(math.pi * i / 24000) ** 2
        samples += struct.pack('<h', round(amplitude * math.sin(2 * math.pi * 660 * i / 48000)))
    return bytes(samples)


def system_speech(text):
    """Local Windows test voice, not the character voice. Never execute text as code."""
    if not isinstance(text, str) or not text.strip() or len(text) > 500:
        raise ValueError('语音文字应为 1～500 字')
    if os.name != 'nt':
        raise ValueError('系统测试音色仅支持 Windows；请配置后续本地 TTS 服务')
    with tempfile.TemporaryDirectory(prefix='tracesoul-speech-') as folder:
        output = Path(folder) / 'speech.wav'
        # Fixed application code; request values travel only through JSON stdin.
        # EncodedCommand executes the .NET call without changing machine script policy.
        code = Path(__file__).with_name('Synthesize-Speech.ps1').read_text(encoding='utf-8')
        result = subprocess.run([
            'powershell.exe', '-NoProfile', '-NonInteractive', '-EncodedCommand',
            base64.b64encode(code.encode('utf-16le')).decode('ascii'),
        ], input=json.dumps({'text': text, 'outputPath': str(output)}), capture_output=True, text=True, timeout=90,
            encoding='utf-8', errors='replace', creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode != 0 or not output.is_file():
            raise RuntimeError('本机系统语音合成失败；未播放')
        with wave.open(str(output), 'rb') as wav:
            if (wav.getframerate(), wav.getnchannels(), wav.getsampwidth()) != (48000, 1, 2):
                raise ValueError('系统语音格式不符合 48kHz PCM16 单声道')
            if not 0 < wav.getnframes() <= 48000 * 120:
                raise ValueError('系统语音为空或超过两分钟')
            pcm = wav.readframes(wav.getnframes())
        return pcm


class Board:
    def __init__(self, port):
        self.port, self.connection = port, None
        self.lock = threading.Lock()

    def close(self):
        if self.connection:
            self.connection.close()
            self.connection = None

    def _connect(self):
        if self.connection is None:
            link = serial.Serial(port=None, baudrate=921600, timeout=0.2, write_timeout=5)
            link.dtr = False
            link.rts = False
            link.port = self.port
            link.open()
            self.connection = link
            time.sleep(0.15)
            link.reset_input_buffer()

    def _reply(self, command_id, status, received=None, timeout=15):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            raw = self.connection.read_until(b'\n', 1024)
            try:
                result = json.loads(raw)
            except (ValueError, UnicodeError):
                continue
            if not isinstance(result, dict) or result.get('id') != command_id or result.get('protocol') != 1:
                continue
            if result.get('status') == 'error':
                raise RuntimeError('开发板返回：' + str(result.get('error', 'unknown')))
            if result.get('status') != status or (received is not None and result.get('received') != received):
                raise RuntimeError('开发板回执顺序或字节数不匹配')
            return result
        raise TimeoutError('等待开发板回执超时；本次结果未知，不会自动重播')

    def transact(self, op, payload=b''):
        with self.lock:
            try:
                self._connect()
                command_id = secrets.randbelow(0x7FFFFFFE) + 1
                header = {'id': command_id, 'op': op, 'size': len(payload), 'checksum': checksum(payload)}
                self.connection.write(json.dumps(header).encode() + b'\n')
                result = self._reply(command_id, 'ready')
                for offset in range(0, len(payload), CHUNK):
                    block = payload[offset:offset + CHUNK]
                    self.connection.write(block)
                    self._reply(command_id, 'chunk', offset + len(block), timeout=8)
                if payload:
                    result = self._reply(command_id, 'done', len(payload), timeout=20)
                return result
            except Exception:
                self.close()
                raise

    def record(self, duration_ms=8000):
        if isinstance(duration_ms, bool) or not isinstance(duration_ms, int) or not 1000 <= duration_ms <= 10000:
            raise ValueError('录音时长必须为 1000～10000 毫秒')
        with self.lock:
            try:
                self._connect()
                command_id = secrets.randbelow(0x7FFFFFFE) + 1
                self.connection.write(json.dumps({'id': command_id, 'op': 'record', 'duration_ms': duration_ms}).encode() + b'\n')
                self._reply(command_id, 'ready')
                captured = self._reply(command_id, 'captured', timeout=duration_ms / 1000 + 10)
                size = duration_ms * 96
                if (captured.get('size') != size or captured.get('sample_rate') != 48000 or
                        not isinstance(captured.get('checksum'), int)):
                    raise RuntimeError('录音规格不匹配')
                data = bytearray()
                for offset in range(0, size, CHUNK):
                    expected = min(CHUNK, size - offset)
                    block = bytearray()
                    deadline = time.monotonic() + 5
                    while len(block) < expected and time.monotonic() < deadline:
                        block.extend(self.connection.read(expected - len(block)))
                    if len(block) != expected:
                        raise TimeoutError('录音上传超时；未自动重新录音')
                    data.extend(block)
                    self.connection.write(json.dumps({'id': command_id, 'ack': len(data)}).encode() + b'\n')
                receipt = self._reply(command_id, 'done', size)
                if checksum(data) != captured['checksum']:
                    raise RuntimeError('录音校验失败；未听写或提交')
                return bytes(data), receipt
            except Exception:
                self.close()
                raise

    def listen(self, utterance, cancelled):
        with self.lock:
            try:
                self._connect()
                command_id = secrets.randbelow(0x7FFFFFFE) + 1
                self.connection.write(json.dumps({'id': command_id, 'op': 'listen'}).encode() + b'\n')
                self._reply(command_id, 'ready')
                total = 0
                deadline = time.monotonic() + 25
                for _ in range(625):
                    total += 3072
                    frame = self._reply(command_id, 'samples', total, timeout=3)
                    if frame.get('size') != 3072 or frame.get('sample_rate') != 48000:
                        raise RuntimeError('麦克风流规格不匹配')
                    block = bytearray()
                    read_deadline = time.monotonic() + 2
                    while len(block) < 3072 and time.monotonic() < read_deadline:
                        block.extend(self.connection.read(3072 - len(block)))
                    if len(block) != 3072 or checksum(block) != frame.get('checksum'):
                        raise RuntimeError('麦克风流校验失败；未听写或提交')
                    if cancelled.is_set():
                        utterance.reason = 'cancelled'
                    elif time.monotonic() >= deadline:
                        utterance.reason = 'transfer_too_slow'
                    else:
                        utterance.feed(block)
                    stop = utterance.reason is not None
                    self.connection.write(json.dumps({'id': command_id, 'ack': total, 'stop': stop}).encode() + b'\n')
                    if stop:
                        return self._reply(command_id, 'done', total, timeout=3)
                raise TimeoutError('麦克风流超限；未提交')
            except Exception:
                self.close()
                raise

    def record_stream(self, duration_ms=8000, cancelled=None, on_progress=None):
        if isinstance(duration_ms, bool) or not isinstance(duration_ms, int) or not 1000 <= duration_ms <= 18000:
            raise ValueError('连续采音时长必须为 1000～18000 毫秒')
        class Capture:
            def __init__(self):
                self.reason = None
                self.data = bytearray()
            def feed(self, block):
                self.data.extend(block)
                if on_progress is not None:
                    on_progress(min(len(self.data) // 96, duration_ms))
                if len(self.data) >= duration_ms * 96:
                    self.reason = 'capture_complete'
        capture = Capture()
        receipt = self.listen(capture, cancelled or threading.Event())
        return bytes(capture.data[:duration_ms * 96]), receipt


def preview(image):
    output = io.BytesIO()
    image.save(output, format='PNG')
    return base64.b64encode(output.getvalue()).decode('ascii')


def make_handler(board, font_path, asr=None, vad=None, speaker=None):
    display = CompanionDisplay(font_path)
    capture_lock = threading.Lock()
    active = {}
    capture_progress = {}
    cancelled_ids = {}  # Brief tombstones close the stop-before-register race.
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def respond(self, status, payload, content_type='application/json; charset=utf-8'):
            data = json.dumps(payload, ensure_ascii=False).encode() if isinstance(payload, dict) else payload
            self.send_response(status)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(data)))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.end_headers()
            self.wfile.write(data)

        def allowed(self):
            port = self.server.server_port
            hosts = {f'127.0.0.1:{port}', f'localhost:{port}'}
            return self.headers.get('Host') in hosts and self.headers.get('Origin') in (
                None, f'http://127.0.0.1:{port}', f'http://localhost:{port}')

        def do_GET(self):
            if not self.allowed():
                return self.respond(403, {'error': '仅允许本机页面访问'})
            if self.path == '/':
                return self.respond(200, Path(__file__).with_name('display-preview.html' if board is None else 'index.html').read_bytes(), 'text/html; charset=utf-8')
            if self.path == '/display':
                return self.respond(200, Path(__file__).with_name('display-preview.html').read_bytes(), 'text/html; charset=utf-8')
            if self.path == '/api/health':
                return self.respond(200, {'service': 'tracesoul-sensory-usb' if board is not None else 'tracesoul-display-preview', 'version': '0.3.0',
                                         'display_version': 1, 'preview_only': board is None,
                                         'asr_ready': asr is not None, 'vad_ready': vad is not None,
                                         'speaker_ready': speaker is not None})
            return self.respond(404, {'error': 'not_found'})

        def do_POST(self):
            if not self.allowed() or self.headers.get('Content-Type', '').split(';')[0] != 'application/json':
                return self.respond(403, {'error': '仅允许本机 JSON 请求'})
            try:
                length = int(self.headers.get('Content-Length', '0'))
                limit=12*1024*1024 if self.path=='/api/speak' else MAX_BODY
                if not 0 < length <= limit:
                    raise ValueError('请求为空或文件太大')
                self.connection.settimeout(10)
                request = json.loads(self.rfile.read(length))
                if not isinstance(request, dict):
                    raise ValueError('请求应为对象')
                if self.path == '/api/display/preview':
                    image, layout = display.render(request)
                    return self.respond(200, {'ok':True, 'preview':preview(image), 'display':layout})
                if board is None:
                    return self.respond(409, {'ok':False, 'error':'此进程仅预览画面，未连接开发板，不能采音或播放'})
                image = None
                layout = None
                if self.path == '/api/display':
                    image, layout = display.render(request)
                    result = board.transact('frame', rgb565(image))
                    layout['preview_only'] = False
                elif self.path == '/api/status':
                    result = board.transact('hello')
                elif self.path == '/api/capture/state':
                    key = request.get('listen_id')
                    if not isinstance(key, str) or not re.fullmatch('[0-9a-f]{32}', key):
                        raise ValueError('无效的录音编号')
                    with capture_lock:
                        state = dict(capture_progress.get(key, {'phase': 'inactive', 'recorded_ms': 0}))
                    return self.respond(200, dict(ok=True, **state))
                elif self.path == '/api/listen/cancel':
                    with capture_lock:
                        key = request.get('listen_id')
                        if not isinstance(key, str) or not re.fullmatch('[0-9a-f]{32}', key):
                            raise ValueError('无效的录音编号')
                        now = time.monotonic()
                        for old in list(cancelled_ids):
                            if cancelled_ids[old] < now - 60:
                                del cancelled_ids[old]
                        if len(cancelled_ids) >= 128:
                            del cancelled_ids[next(iter(cancelled_ids))]
                        cancelled_ids[key] = now
                        event = active.get(request.get('listen_id'))
                        if event is not None:
                            event.set()
                    return self.respond(200, {'ok': True})
                elif self.path in ('/api/utterance', '/api/voice/sample'):
                    if asr is None or vad is None or speaker is None:
                        raise ValueError('本地听写、语音检测或声纹模型未就绪；未录音')
                    listen_id = request.get('listen_id')
                    if not isinstance(listen_id, str) or not re.fullmatch('[0-9a-f]{32}', listen_id):
                        raise ValueError('无效的录音编号')
                    event = threading.Event()
                    with capture_lock:
                        if active:
                            raise ValueError('已有录音进行中')
                        if listen_id in cancelled_ids:
                            event.set()
                        active[listen_id] = event
                        capture_progress[listen_id] = {'phase': 'preparing', 'recorded_ms': 0}
                    try:
                        if self.path == '/api/voice/sample':
                            # Enrollment and diagnostics use an 8s window with bounded
                            # stream protocol instead of the old <=10s record buffer.
                            def update_progress(recorded_ms):
                                with capture_lock:
                                    capture_progress[listen_id] = {'phase': 'recording', 'recorded_ms': recorded_ms}
                            pcm, receipt = board.record_stream(8000, event, on_progress=update_progress)
                            with capture_lock:
                                capture_progress[listen_id] = {'phase': 'processing', 'recorded_ms': len(pcm) // 96}
                            voiced, segments = vad.analyze(pcm) if not event.is_set() else (b'', [])
                            voice = speaker.evidence(voiced) if voiced else None
                            import numpy as np
                            samples = np.frombuffer(pcm, dtype='<i2').astype(np.float32)
                            quality = {'recorded_ms': len(pcm) // 96, 'speech_ms': len(voiced) // 32,
                                       'rms': round(float(np.sqrt(np.mean(samples * samples))), 2) if len(samples) else 0,
                                       'peak': int(np.max(np.abs(samples))) if len(samples) else 0,
                                       'segments': segments, 'capture_id': receipt.get('id'),
                                       'audio_fingerprint': hashlib.sha256(pcm).hexdigest()[:12],
                                       'volume_500ms': [round(float(np.sqrt(np.mean(part * part))), 1)
                                           for start in range(0, len(samples), 24000)
                                           if len(part := samples[start:start + 24000])]}
                            if not event.is_set():
                                # Local preview only, never a Moment or an identity decision.
                                quality['full_text'] = asr.transcribe(pcm).get('text', '')
                            if voice is not None:
                                vectors = np.asarray([w['embedding'] for w in voice['windows']])
                                quality['window_to_clip_min'] = round(float(np.min(vectors @ np.asarray(voice['embedding']))), 4)
                            return self.respond(200, {'ok': True, 'receipt': receipt, 'voice': voice, 'quality': quality})
                        utterance = vad.create()
                        receipt = board.listen(utterance, event)
                        voice = None
                        result = {'text': '', 'status': utterance.reason}
                        if utterance.reason == 'speech_end' and not event.is_set():
                            voice = speaker.evidence(utterance.pcm)
                            # Short wake names can be recognized without sufficient speaker evidence.
                            # Host still requires an owner match before admitting any conversation.
                            result = asr.transcribe(utterance.pcm, 16000)
                        if event.is_set():
                            result = {'text': '', 'status': 'cancelled'}
                            voice = None
                        return self.respond(200, dict(ok=True, receipt=receipt, voice=voice,
                            started_ms=utterance.started_at * 32 if utterance.started_at is not None else None,
                            recorded_ms=utterance.frames * 32, **result))
                    finally:
                        with capture_lock:
                            active.pop(listen_id, None)
                            capture_progress.pop(listen_id, None)
                elif self.path == '/api/listen':
                    if asr is None:
                        raise ValueError('本地听写模型尚未加载，未开始录音')
                    duration = request.get('duration_ms', 8000)
                    # Explicit finite capture only. No always-on recording and no automatic Moment.
                    pcm, receipt = board.record(duration)
                    return self.respond(200, dict(ok=True, receipt=receipt, **asr.transcribe(pcm)))
                elif self.path == '/api/text':
                    image = text_frame(request.get('text'), font_path)
                    result = board.transact('frame', rgb565(image))
                elif self.path == '/api/image':
                    image = picture_frame(base64.b64decode(request['data'], validate=True), font_path)
                    result = board.transact('frame', rgb565(image))
                elif self.path == '/api/audio':
                    result = board.transact('audio', decode_wav(base64.b64decode(request['data'], validate=True)))
                elif self.path == '/api/chime':
                    result = board.transact('audio', chime())
                elif self.path == '/api/speak':
                    external = 'data' in request
                    pcm = decode_wav(base64.b64decode(request['data'], validate=True), max_seconds=120) if external else system_speech(request.get('text'))
                    receipts = []
                    display_receipts = []
                    display_request = request.get('display')
                    if display_request is not None:
                        if not isinstance(display_request,dict):raise ValueError('显示请求应为对象')
                        display_request = dict(display_request,state='speaking',page=0)
                        # Validate and render every page before beginning any physical playback.
                        _, layout = display.render(display_request)
                        pages = [display.render(dict(display_request,page=page))[0] for page in range(layout['page_count'])]
                        # Each page gets a contiguous audio interval, split into <=8s board chunks.
                        # This is approximate page timing, not word-level forced alignment.
                        samples=len(pcm)//2
                        for page,frame in enumerate(pages):
                            display_receipts.append(board.transact('frame',rgb565(frame)))
                            first=(samples*page//len(pages))*2;last=(samples*(page+1)//len(pages))*2
                            for offset in range(first,last,48000*2*8):
                                receipts.append(board.transact('audio',pcm[offset:min(last,offset+48000*2*8)]))
                        return self.respond(200,{'ok':True,'receipts':receipts,'display_receipts':display_receipts,
                            'engine':'外部角色语音' if external else 'Windows 系统测试音色','audio_ms':len(pcm)*1000//96000,
                            'display_pages':len(pages),'display_complete':True})
                    # Board accepts at most ten seconds. No automatic repeat on unknown results.
                    for offset in range(0, len(pcm), 48000 * 2 * 8):
                        receipts.append(board.transact('audio', pcm[offset:offset + 48000 * 2 * 8]))
                    return self.respond(200, {'ok': True, 'receipts': receipts,
                                             'engine': '外部角色语音' if external else 'Windows 系统测试音色',
                                             'audio_ms': len(pcm) * 1000 // 96000})
                else:
                    return self.respond(404, {'error': 'not_found'})
                response = {'ok': True, 'receipt': result}
                if self.path == '/api/status':response['display_version']=1
                if layout is not None:
                    response['display'] = layout
                if image is not None:
                    response['preview'] = preview(image)
                self.respond(200, response)
            except (ValueError, KeyError, TypeError, wave.Error, Image.DecompressionBombError) as error:
                self.respond(400, {'ok': False, 'error': str(error)})
            except Exception as error:
                self.respond(503, {'ok': False, 'error': str(error)})
    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', help='USB serial port, e.g. COM3; not used in preview-only mode')
    parser.add_argument('--preview-only', action='store_true', help='No USB object, recording, playback or ASR models')
    parser.add_argument('--http-port', type=int, default=5089)
    parser.add_argument('--font')
    parser.add_argument('--asr-model-directory', help='Optional local SenseVoice int8 directory')
    args = parser.parse_args()
    if not args.preview_only and not args.port:
        parser.error('--port is required unless --preview-only is set')
    if args.preview_only and (args.port or args.asr_model_directory):
        parser.error('preview-only cannot use a serial port or ASR models')
    board = None if args.preview_only else Board(args.port)
    asr = vad = speaker = None
    if args.asr_model_directory:
        from local_asr import LocalAsr
        asr = LocalAsr(args.asr_model_directory)
        directory = Path(args.asr_model_directory)
        vad_model = directory / 'silero_vad.onnx'
        speaker_model = directory.parent / 'speaker/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx'
        if vad_model.is_file():
            from local_vad import LocalVad
            vad = LocalVad(vad_model)
        if speaker_model.is_file():
            from local_speaker import LocalSpeaker
            speaker = LocalSpeaker(speaker_model)
    server = ThreadingHTTPServer(('127.0.0.1', args.http_port), make_handler(board, choose_font(args.font), asr, vad, speaker))
    print(f'感官终端控制台 http://127.0.0.1:{args.http_port}', flush=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()
        if board is not None:
            board.close()


if __name__ == '__main__':
    main()
