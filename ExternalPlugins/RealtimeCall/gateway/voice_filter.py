"""Local target-speaker enrollment and pre-ASR filtering for trusted call gateways.

Raw audio stays in memory. Only enrollment explicitly writes a voiceprint. Importing
this module does not download a model, open a microphone, or connect to a server.
"""
from __future__ import annotations

import argparse
import array
import asyncio
import json
import math
import os
from pathlib import Path
import sys
import tempfile
import wave

MODEL = "speechbrain/spkrec-ecapa-voxceleb"
DIMENSIONS = 192
RATE = 16000
FRAME_BYTES = 640  # 20 ms, mono PCM16 little-endian
MIN_SPEECH_MS = 800
MAX_AUDIO_BYTES = RATE * 2 * 10


class VoiceError(ValueError):
    pass


def normalize(vector):
    if not isinstance(vector, (list, tuple)) or len(vector) != DIMENSIONS:
        raise VoiceError("invalid_embedding")
    if any(type(x) not in (int, float) or not math.isfinite(x) or abs(x) > 10000 for x in vector):
        raise VoiceError("invalid_embedding")
    norm = math.sqrt(sum(x * x for x in vector))
    if norm < 1e-8:
        raise VoiceError("invalid_embedding")
    return [x / norm for x in vector]


def cosine(a, b):
    return sum(x * y for x, y in zip(a, b))


def validate_threshold(value):
    if not math.isfinite(value) or not 0.4 <= value <= 0.95:
        raise VoiceError("invalid_threshold")
    return value


def load_profile(path):
    try:
        path = Path(path)
        if path.stat().st_size > 32768:
            raise VoiceError("voiceprint_not_enrolled")
        profile = json.loads(path.read_text(encoding="utf-8"))
        if (not isinstance(profile, dict) or profile.get("model") != MODEL
                or type(profile.get("sample_count")) is not int or profile["sample_count"] < 3
                or type(profile.get("speech_ms")) is not int or profile["speech_ms"] < 15000):
            raise VoiceError("voiceprint_not_enrolled")
        return normalize(profile.get("embedding"))
    except (OSError, ValueError, TypeError):
        raise VoiceError("voiceprint_not_enrolled") from None


def pcm_samples(pcm):
    if len(pcm) % 2:
        raise VoiceError("invalid_pcm")
    samples = array.array("h", pcm)
    if sys.byteorder != "little":
        samples.byteswap()
    return samples


class SpeechEncoder:
    """Models are loaded once, lazily, from the official SpeechBrain model card."""
    def __init__(self, cache):
        self.cache = str(cache)
        self.classifier = None

    def __call__(self, pcm):
        import torch
        if self.classifier is None:
            from speechbrain.inference.speaker import SpeakerRecognition
            from speechbrain.utils.fetching import LocalStrategy
            self.classifier = SpeakerRecognition.from_hparams(
                source=MODEL, savedir=self.cache, run_opts={"device": "cpu"},
                local_strategy=LocalStrategy.COPY)
        samples = torch.tensor(pcm_samples(pcm), dtype=torch.float32) / 32768.0
        with torch.inference_mode():
            embedding = self.classifier.encode_batch(samples.unsqueeze(0))
        return normalize(embedding.squeeze().tolist())


class VoiceFilter:
    def __init__(self, encoder, threshold=0.65, vad_factory=None):
        self.encoder = encoder
        self.threshold = validate_threshold(threshold)
        self.vad_factory = vad_factory

    def speech(self, pcm):
        if not isinstance(pcm, bytes) or len(pcm) % 2 or len(pcm) > MAX_AUDIO_BYTES:
            raise VoiceError("invalid_pcm")
        if self.vad_factory is None:
            import webrtcvad
            vad = webrtcvad.Vad(3)
        else:
            vad = self.vad_factory()
        frames = []
        for offset in range(0, len(pcm) - FRAME_BYTES + 1, FRAME_BYTES):
            frame = pcm[offset:offset + FRAME_BYTES]
            samples = pcm_samples(frame)
            rms = math.sqrt(sum(x * x for x in samples) / len(samples)) / 32768
            # Energy alone cannot reject television speech; VAD + speaker checks follow.
            if rms >= 0.005 and vad.is_speech(frame, RATE):
                frames.append(frame)
        return b"".join(frames)

    def evidence(self, pcm, playback_active=False):
        # Until acoustic echo cancellation is integrated, use conservative half duplex.
        if playback_active:
            return None, "playback_echo_guard", b""
        speech = self.speech(pcm)
        duration = len(speech) * 1000 // (RATE * 2)
        if duration < MIN_SPEECH_MS:
            return None, "insufficient_speech", b""
        # Divide ALL retained speech into evenly sized windows; don't drop a different
        # speaker at the end or accept a whole mixed sentence on one average vector.
        count = math.ceil(duration / 2500)
        total_frames = len(speech) // FRAME_BYTES
        windows = []
        for i in range(count):
            start = (i * total_frames // count) * FRAME_BYTES
            end = ((i + 1) * total_frames // count) * FRAME_BYTES
            chunk = speech[start:end]
            windows.append({"speech_ms": len(chunk) * 1000 // (RATE * 2),
                            "embedding": [round(x, 6) for x in normalize(self.encoder(chunk))]})
        return {"model": MODEL, "playback_active": False, "windows": windows}, None, speech

    def verify(self, pcm, profile_path, playback_active=False):
        profile = load_profile(profile_path)  # Reload so removing/replacing enrollment takes effect.
        evidence, reason, speech = self.evidence(pcm, playback_active)
        if reason:
            return None, reason, b""
        if any(cosine(profile, normalize(w["embedding"])) < self.threshold for w in evidence["windows"]):
            return None, "speaker_mismatch", b""
        return evidence, None, speech

    def prepare_transcript(self, pcm, profile_path, transcribe, playback_active=False):
        """Call ASR only after verification; ASR sees exactly the checked voiced PCM.

        The gateway adds seq/op/session_id/generation and sends this object to the
        existing WebSocket. Never attach evidence from a different audio segment.
        """
        evidence, reason, speech = self.verify(pcm, profile_path, playback_active)
        if reason:
            return None, reason
        text = transcribe(speech).strip()
        if not text or len(text) > 4000:
            return None, "invalid_text"
        return {"text": text, "voice": evidence}, None

    def enroll(self, recordings, output, replace=False):
        if not 3 <= len(recordings) <= 8:
            raise VoiceError("need_3_to_8_recordings")
        vectors, duration = [], 0
        for pcm in recordings:
            evidence, reason, _ = self.evidence(pcm)
            if reason:
                raise VoiceError(reason)
            windows = evidence["windows"]
            sample_ms = sum(w["speech_ms"] for w in windows)
            if sample_ms < 5000:
                raise VoiceError("need_5_seconds_speech_per_recording")
            duration += sample_ms
            vectors.extend(normalize(w["embedding"]) for w in windows)
        # Refuse contaminated enrollment, rather than learning every speaker in a room.
        if any(cosine(a, b) < self.threshold for i, a in enumerate(vectors) for b in vectors[i + 1:]):
            raise VoiceError("inconsistent_enrollment")
        centroid = normalize([sum(v[i] for v in vectors) / len(vectors) for i in range(DIMENSIONS)])
        profile = {"model": MODEL, "sample_count": len(recordings), "speech_ms": duration,
                   "embedding": centroid}
        output = Path(output)
        output.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(prefix=".voiceprint-", dir=output.parent)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                json.dump(profile, handle, allow_nan=False)
            if replace:
                os.replace(temporary, output)
            else:
                # Atomic no-overwrite installation. Existing enrollment is never replaced implicitly.
                os.link(temporary, output)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)


async def keepalive(request, stopped, interval=10):
    """Run independently of audio/VAD/ASR. request allocates seq and waits for ack.

    A failed ping must propagate to the caller's connection cleanup. Run synchronous
    model/ASR work with asyncio.to_thread so silence or inference cannot block pings.
    """
    if not 0 < interval <= 10:
        raise ValueError("invalid_keepalive_interval")
    while not stopped.is_set():
        await request({"op": "ping"})
        try:
            await asyncio.wait_for(stopped.wait(), timeout=interval)
        except asyncio.TimeoutError:
            pass


def read_wav(path):
    with wave.open(str(path), "rb") as source:
        if (source.getnchannels() != 1 or source.getsampwidth() != 2
                or source.getframerate() != RATE or source.getcomptype() != "NONE"
                or source.getnframes() > RATE * 10):
            raise VoiceError("require_mono_pcm16_16000hz_wav_under_10_seconds")
        return source.readframes(source.getnframes())


def main():
    parser = argparse.ArgumentParser(description="本人声纹：本地录入、验证与麦克风检测（不发起电话）")
    parser.add_argument("--cache", default=str(Path.home() / ".cache/tracesoul2/ecapa"))
    parser.add_argument("--threshold", type=float, default=0.65)
    commands = parser.add_subparsers(dest="command", required=True)
    enroll = commands.add_parser("enroll", help="从三到八份 WAV 录入，每份至少五秒有效人声")
    enroll.add_argument("--output", required=True)
    enroll.add_argument("--replace", action="store_true")
    enroll.add_argument("wav", nargs="+")
    record = commands.add_parser("record-enroll", help="录制三段八秒麦克风音频，只保存声纹")
    record.add_argument("--output", required=True)
    record.add_argument("--replace", action="store_true")
    record.add_argument("--device")
    check = commands.add_parser("check", help="只输出匹配结果，不输出声纹或转写")
    check.add_argument("--profile", required=True)
    check.add_argument("wav")
    listen = commands.add_parser("listen", help="麦克风实时匹配预览，不转写、不保存音频")
    listen.add_argument("--profile", required=True)
    listen.add_argument("--device")
    args = parser.parse_args()
    voice = VoiceFilter(SpeechEncoder(args.cache), args.threshold)
    if args.command in ("record-enroll", "enroll"):
        if Path(args.output).exists() and not args.replace:
            raise VoiceError("profile_exists_use_replace")
        if args.command == "enroll":
            recordings = [read_wav(path) for path in args.wav]
        else:
            import sounddevice as sd
            recordings = []
            for i in range(3):
                input(f"第 {i + 1}/3 段：安静环境中正常说不同内容，按回车开始录制八秒。")
                audio = sd.rec(8 * RATE, samplerate=RATE, channels=1, dtype="int16", device=args.device)
                sd.wait()
                recordings.append(audio.astype("<i2").tobytes())
        voice.enroll(recordings, args.output, args.replace)
        print("声纹已保存；原始音频未另行保存。")
    elif args.command == "check":
        _, reason, _ = voice.verify(read_wav(args.wav), args.profile)
        print(json.dumps({"accepted": reason is None, "reason": reason}))
    else:
        import sounddevice as sd
        load_profile(args.profile)
        print("麦克风声纹预览，Ctrl+C 退出。每两秒检查一次；不发起通话。")
        with sd.RawInputStream(samplerate=RATE, channels=1, dtype="int16", device=args.device) as stream:
            while True:
                pcm, overflowed = stream.read(RATE * 2)
                if overflowed:
                    print("audio_overflow")
                    continue
                _, reason, _ = voice.verify(bytes(pcm), args.profile)
                print("本人声音" if reason is None else reason)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except VoiceError as error:
        print(str(error), file=sys.stderr)
        sys.exit(2)
