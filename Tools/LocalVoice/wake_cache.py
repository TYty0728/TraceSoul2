"""Private, bounded final-WAV cache for explicitly tagged short wake acknowledgements."""
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import re
import secrets
import time
import wave


class WakeAudioCache:
    VERSION = 1
    LIMIT = 16
    MAX_AUDIO = 4 * 1024 * 1024

    def __init__(self, backend, failure):
        self.backend, self.failure = backend, failure
        self.folder = (backend.runtime / 'cache' / 'wake-audio').resolve()
        if not self.folder.is_relative_to(backend.runtime):
            raise failure('invalid_wake_cache_path')

    def identity(self, request):
        audio, transcript, _ = self.backend.reference(request.voice)
        recipe = request.model_dump()
        recipe['input'] = request.input.strip()
        value = dict(version=self.VERSION, recipe=recipe, model_stamp=self.backend.model_stamp,
                     reference=str(audio), audio_sha256=hashlib.sha256(audio.read_bytes()).hexdigest(),
                     transcript_sha256=hashlib.sha256(transcript.encode()).hexdigest())
        return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False).encode()).hexdigest()

    def audio_ms(self, audio):
        if not 44 <= len(audio) <= self.MAX_AUDIO:
            raise self.failure('invalid_wake_audio')
        try:
            with wave.open(io.BytesIO(audio), 'rb') as reader:
                frames, rate, channels = reader.getnframes(), reader.getframerate(), reader.getnchannels()
                if reader.getsampwidth() != 2 or channels not in (1, 2) or not 8000 <= rate <= 96000 or not 0 < frames <= rate * 30:
                    raise ValueError()
                if len(reader.readframes(frames)) != frames * channels * 2:
                    raise ValueError()
                return round(frames * 1000 / rate)
        except (ValueError, EOFError, wave.Error):
            raise self.failure('invalid_wake_audio') from None

    def read(self, key):
        path = self.folder / (key + '.json')
        try:
            if path.resolve().parent != self.folder or path.stat().st_size > 6 * 1024 * 1024:
                return None
            value = json.loads(path.read_text(encoding='utf-8'))
            if value['version'] != self.VERSION or value['key'] != key:
                return None
            audio = base64.b64decode(value['wav'], validate=True)
            if hashlib.sha256(audio).hexdigest() != value['sha256']:
                return None
            return audio, self.audio_ms(audio)
        except (OSError, ValueError, KeyError, TypeError, self.failure):
            return None

    def write(self, key, audio):
        from access import protect_file
        self.folder.mkdir(parents=True, exist_ok=True, mode=0o700)
        temporary = self.folder / (key + '.' + secrets.token_hex(8) + '.tmp')
        target = self.folder / (key + '.json')
        value = dict(version=self.VERSION, key=key, sha256=hashlib.sha256(audio).hexdigest(),
                     wav=base64.b64encode(audio).decode('ascii'))
        try:
            with temporary.open('xb') as output:
                protect_file(temporary)
                output.write(json.dumps(value).encode())
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary, target)
        finally:
            temporary.unlink(missing_ok=True)
        # Only our own resolved, hash-named entries in this fixed runtime directory are pruned.
        files = [p for p in self.folder.glob('*.json') if re.fullmatch(r'[0-9a-f]{64}\.json', p.name)
                 and p.resolve().parent == self.folder and p.is_file()]
        for path in sorted(files, key=lambda p: p.stat().st_mtime_ns, reverse=True)[self.LIMIT:]:
            path.unlink(missing_ok=True)

    def synthesize(self, request):
        if len(request.input.strip()) > 30:
            raise self.failure('wake_text_too_long')
        if self.backend.model is None:
            raise self.failure('model_not_ready')
        started = time.perf_counter()
        key = self.identity(request)
        found = self.read(key)
        if found is not None and self.identity(request) == key:
            audio, duration = found
            return audio, dict(synthesis_ms=0, audio_ms=duration, rtf=0,
                               cache_lookup_ms=round((time.perf_counter()-started)*1000)), True
        if not self.backend.lock.acquire(blocking=False):
            raise self.failure('busy')
        try:
            # Selection changes and concurrent fills must be rechecked under the synthesis lock.
            key = self.identity(request)
            found = self.read(key)
            if found is not None:
                audio, duration = found
                return audio, dict(synthesis_ms=0, audio_ms=duration, rtf=0), True
            audio, timing = self.backend._synthesize_locked(request)
            self.audio_ms(audio)
            if self.identity(request) != key:
                raise self.failure('voice_changed_during_generation')
            self.write(key, audio)
            return audio, timing, False
        finally:
            self.backend.lock.release()
