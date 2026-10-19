"""Loopback Qwen3-TTS voice-clone service. No QQ, cloud TTS or automatic recordings."""
from __future__ import annotations

import argparse
import asyncio
from contextlib import asynccontextmanager
import io
import hashlib
import json
import os
from pathlib import Path
import re
import threading
import time

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import HTMLResponse, Response
from pydantic import BaseModel, ConfigDict, Field
from starlette.middleware.trustedhost import TrustedHostMiddleware

MODEL_NAME = "Qwen3-TTS-12Hz-0.6B-Base"
VOICE_ID = re.compile(r"^[a-zA-Z0-9_-]{1,48}$")


class VoiceFailure(Exception):
    pass


class SpeechRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    model: str = "qwen3-tts-local"
    input: str = Field(min_length=1, max_length=240)
    voice: str = "character"
    response_format: str = "wav"
    language: str = "Chinese"
    seed: int = Field(default=0, ge=0, le=2147483647)
    temperature: float = Field(default=0.8, ge=0.2, le=1.2)
    top_p: float = Field(default=0.95, ge=0.5, le=1.0)
    max_new_tokens: int = Field(default=750, ge=64, le=750)


class LocalVoice:
    def __init__(self, runtime: Path, accelerate=True):
        self.runtime = runtime.resolve()
        self.model = None
        self.prompts = {}
        self.lock = threading.Lock()
        self.device = "cuda:0"
        self.phase = "idle"
        self.started = None
        self.accelerate = accelerate
        self.acceleration = 'standard'
        self.stage_times = {}
        self.last_timing = None
        self.model_stamp = 'unloaded'
        from wake_cache import WakeAudioCache
        self.wake_cache = WakeAudioCache(self, VoiceFailure)

    def load(self):
        # All model assets must already exist locally. A request never downloads weights.
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        import torch
        from qwen_tts import Qwen3TTSModel
        if not torch.cuda.is_available():
            raise VoiceFailure("cuda_unavailable")
        torch.set_num_threads(4)
        self.model = Qwen3TTSModel.from_pretrained(
            str(self.runtime / "models" / MODEL_NAME), device_map=self.device,
            dtype=torch.bfloat16, attn_implementation="sdpa", local_files_only=True)
        model_files = self.runtime / "models" / MODEL_NAME
        stamps = [(p.name, p.stat().st_size, p.stat().st_mtime_ns) for p in sorted(model_files.iterdir())
                  if p.is_file() and p.suffix in ('.json', '.safetensors')]
        self.model_stamp = hashlib.sha256(json.dumps([MODEL_NAME, torch.__version__, stamps]).encode()).hexdigest()
        self.model.model.talker.generation_config.max_time = 90
        if self.accelerate:
            from predictor_graph import enable_predictor_graph
            self.acceleration = enable_predictor_graph(self.model)
        self._time_stage(self.model.model, 'generate', 'tokens_ms', 'generating_tokens')
        self._time_stage(self.model.model.speech_tokenizer, 'decode', 'decode_ms', 'decoding_audio')

    def _time_stage(self, target, name, metric, phase):
        original = getattr(target, name)
        def measured(*args, **kwargs):
            import torch
            self.phase = phase
            torch.cuda.synchronize()
            started = time.perf_counter()
            result = original(*args, **kwargs)
            torch.cuda.synchronize()
            self.stage_times[metric] = round((time.perf_counter()-started)*1000)
            return result
        setattr(target, name, measured)

    def reference(self, voice: str):
        if not VOICE_ID.fullmatch(voice):
            raise VoiceFailure("invalid_voice")
        root = (self.runtime / "voices").resolve()
        folder = (root / voice).resolve()
        if voice == "character" and (folder / "active.json").exists():
            try:
                pointer = folder / "active.json"
                if pointer.stat().st_size > 4096:
                    raise ValueError()
                selected = json.loads(pointer.read_text(encoding="utf-8"))["voice"]
                if not VOICE_ID.fullmatch(selected) or selected == "character":
                    raise ValueError()
                folder = (root / selected).resolve()
            except (OSError, ValueError, KeyError, TypeError):
                raise VoiceFailure("invalid_voice_selection") from None
        if folder.parent != root:
            raise VoiceFailure("invalid_voice")
        audio = folder / "reference.wav"
        transcript = folder / "reference.txt"
        if not audio.is_file() or not transcript.is_file():
            raise VoiceFailure("voice_reference_missing")
        if audio.resolve().parent != folder or transcript.resolve().parent != folder:
            raise VoiceFailure("invalid_voice")
        if audio.stat().st_size > 12 * 1024 * 1024 or transcript.stat().st_size > 16000:
            raise VoiceFailure("reference_too_large")
        text = transcript.read_text(encoding="utf-8-sig").strip()
        if not text or len(text) > 2000:
            raise VoiceFailure("reference_text_missing")
        stamp = (str(audio), audio.stat().st_mtime_ns, audio.stat().st_size,
                 transcript.stat().st_mtime_ns, transcript.stat().st_size)
        return audio, text, stamp

    def voices(self):
        entries = []
        root = self.runtime / "voices"
        ids = {"character", "demo"}
        if root.exists():
            ids.update(p.name for p in root.iterdir() if p.is_dir() and VOICE_ID.fullmatch(p.name) and not p.name.startswith('lab_'))
        for voice in sorted(ids):
            try:
                self.reference(voice)
                ready = True
            except (VoiceFailure, OSError, UnicodeError):
                ready = False
            entries.append({"id": voice, "ready": ready,
                            "label": "角色音色" if voice == "character" else "官方样例（仅验证）" if voice == "demo" else "原始角色参考" if voice == "source_remote" else voice})
        return entries

    def synthesize(self, request: SpeechRequest):
        if not self.lock.acquire(blocking=False):
            raise VoiceFailure("busy")
        try:
            return self._synthesize_locked(request)
        finally:
            self.lock.release()

    def _synthesize_locked(self, request: SpeechRequest):
        self.started = time.perf_counter()
        self.phase = "reference_features"
        self.stage_times = {}
        try:
            if self.model is None:
                raise VoiceFailure("model_not_ready")
            import numpy as np
            import soundfile as sf
            import torch
            audio, ref_text, stamp = self.reference(request.voice)
            started = time.perf_counter()
            # Prompt features are reused until reference files change.
            cached = self.prompts.get(request.voice)
            with torch.inference_mode(), torch.random.fork_rng(devices=[0]):
                torch.manual_seed(request.seed)
                if cached is None or cached[0] != stamp:
                    info = sf.info(str(audio))
                    if not 3 <= info.duration <= 30 or info.channels not in (1, 2):
                        raise VoiceFailure("reference_must_be_3_to_30_seconds")
                    samples, sample_rate = sf.read(str(audio), dtype="float32", always_2d=False)
                    if not np.isfinite(samples).all() or float(np.max(np.abs(samples))) < 0.001:
                        raise VoiceFailure("invalid_reference_audio")
                    if samples.ndim == 2:
                        samples = samples.mean(axis=1)
                    prompt = self.model.create_voice_clone_prompt(
                        ref_audio=(samples, sample_rate), ref_text=ref_text, x_vector_only_mode=False)
                    if len(self.prompts) >= 4:
                        self.prompts.pop(next(iter(self.prompts)))
                    self.prompts[request.voice] = (stamp, prompt)
                else:
                    prompt = cached[1]
                torch.cuda.synchronize()
                self.stage_times['reference_ms'] = round((time.perf_counter()-started)*1000)
                self.phase = "generating"
                generation_started = time.perf_counter()
                wavs, rate = self.model.generate_voice_clone(
                    text=request.input.strip(), language=request.language,
                    voice_clone_prompt=prompt, non_streaming_mode=True,
                    do_sample=True, subtalker_dosample=True,
                    temperature=request.temperature, top_p=request.top_p,
                    subtalker_temperature=request.temperature, subtalker_top_p=request.top_p,
                    max_new_tokens=request.max_new_tokens)
                if time.perf_counter() - generation_started >= 90:
                    raise VoiceFailure("generation_time_limit")
                torch.cuda.synchronize()
            self.phase = "encoding_audio"
            waveform = np.asarray(wavs[0], dtype=np.float32)
            if waveform.size == 0 or not np.isfinite(waveform).all():
                raise VoiceFailure("invalid_generated_audio")
            duration = len(waveform) / rate
            wav_started = time.perf_counter()
            output = io.BytesIO()
            sf.write(output, waveform, rate, format="WAV", subtype="PCM_16")
            self.stage_times['wav_ms'] = round((time.perf_counter()-wav_started)*1000)
            elapsed = time.perf_counter() - started
            timing = {"synthesis_ms": round(elapsed * 1000), "audio_ms": round(duration * 1000),
                      "rtf": round(elapsed / duration, 3), **self.stage_times}
            self.last_timing = timing.copy()
            return output.getvalue(), timing
        finally:
            self.phase = "idle"
            self.started = None


def create_app(backend: LocalVoice, load_model=True):
    @asynccontextmanager
    async def lifespan(_):
        if load_model:
            await asyncio.to_thread(backend.load)
        yield

    app = FastAPI(title="TraceSoul2 本地语音", lifespan=lifespan, docs_url=None, redoc_url=None)
    from access import attach_access
    attach_access(app, backend)
    from voice_lab import attach_voice_lab
    attach_voice_lab(app, backend, SpeechRequest, VoiceFailure)

    @app.middleware("http")
    async def loopback_only(request: Request, call_next):
        origin = request.headers.get("origin")
        if origin and origin != f"{request.url.scheme}://{request.headers.get('host')}":
            return Response(status_code=403)
        return await call_next(request)

    @app.get("/", response_class=HTMLResponse)
    def home():
        return (Path(__file__).parent / "index.html").read_text(encoding="utf-8")

    @app.get("/health")
    def health():
        started=backend.started
        return {"ready": backend.model is not None, "model": MODEL_NAME,
                "authentication_required": True,
                "device": backend.device, "busy": backend.lock.locked(),
                "phase": backend.phase, "elapsed_ms": round((time.perf_counter()-started)*1000) if started else 0,
                "streaming": False, "acceleration": backend.acceleration,
                "last_timing": backend.last_timing, "voices": backend.voices()}

    @app.get("/v1/models")
    def models():
        return {"object": "list", "data": [{"id": "qwen3-tts-local", "object": "model"}]}

    @app.post("/v1/audio/speech")
    @app.post("/ui/speech")
    async def speech(request: SpeechRequest, http_request: Request):
        if not request.input.strip():
            raise HTTPException(400, "empty_input")
        if request.model not in ("qwen3-tts-local", MODEL_NAME):
            raise HTTPException(400, "unsupported_model")
        if request.response_format != "wav":
            raise HTTPException(400, "only_wav_supported")
        if request.language not in ("Chinese", "English", "Auto"):
            raise HTTPException(400, "unsupported_language")
        try:
            cache_hint = http_request.headers.get('x-tracesoul-wake-cache')
            if cache_hint is not None and cache_hint != 'v1':
                raise VoiceFailure('unsupported_wake_cache_version')
            if cache_hint:
                audio, timing, cache_hit = await asyncio.to_thread(backend.wake_cache.synthesize, request)
            else:
                audio, timing = await asyncio.to_thread(backend.synthesize, request)
        except VoiceFailure as error:
            code = str(error)
            raise HTTPException(429 if code == "busy" else 503 if code == "model_not_ready" else 400, code) from None
        except Exception as error:
            # Type only: no user text, voice reference, local paths or model dumps in logs/responses.
            print("Synthesis failed:", type(error).__name__, flush=True)
            raise HTTPException(500, "synthesis_failed") from None
        return Response(audio, media_type="audio/wav", headers={
            "Cache-Control": "no-store", "X-Synthesis-Ms": str(timing["synthesis_ms"]),
            "X-Audio-Ms": str(timing["audio_ms"]), "X-RTF": str(timing["rtf"]),
            **({"X-TraceSoul-Wake-Cache": "hit" if cache_hit else "miss"} if cache_hint else {}),
            **({"X-Cache-Lookup-Ms": str(timing["cache_lookup_ms"])} if "cache_lookup_ms" in timing else {}),
            **{f"X-{key.replace('_','-')}":str(timing[key]) for key in ('reference_ms','tokens_ms','decode_ms','wav_ms') if key in timing}})

    app.add_middleware(TrustedHostMiddleware, allowed_hosts=["127.0.0.1", "localhost"])
    return app


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--port", type=int, default=5088)
    parser.add_argument("--no-predictor-graph", action='store_true')
    args = parser.parse_args()
    import uvicorn
    uvicorn.run(create_app(LocalVoice(args.runtime, accelerate=not args.no_predictor_graph)), host="127.0.0.1", port=args.port, access_log=False)


if __name__ == "__main__":
    main()
