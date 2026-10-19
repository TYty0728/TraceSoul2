"""Local voice variants. Candidates never replace the active voice until selected."""
import asyncio
import json
from pathlib import Path
import re
import shutil
import time
import uuid

from fastapi import HTTPException
from fastapi.responses import FileResponse, Response
from pydantic import BaseModel, ConfigDict, Field


class CardRequest(BaseModel):
    model_config = ConfigDict(extra='forbid')
    text: str = Field(min_length=1, max_length=120)
    voice: str = Field(default='source_remote', pattern=r'^[a-zA-Z0-9_-]{1,48}$')
    seed: int = Field(ge=0, le=2147483647)
    temperature: float = Field(default=0.8, ge=0.2, le=1.2)
    top_p: float = Field(default=0.95, ge=0.5, le=1.0)


class Selection(BaseModel):
    model_config = ConfigDict(extra='forbid')
    id: str = Field(pattern=r'^[0-9a-f]{32}$')


class PreviewRequest(BaseModel):
    model_config = ConfigDict(extra='forbid')
    text: str = Field(min_length=1, max_length=240)


class VoiceLab:
    def __init__(self, backend, speech_type, failure_type):
        self.backend, self.speech_type, self.failure_type = backend, speech_type, failure_type
        self.root = backend.runtime / 'lab' / 'cards'

    def card_path(self, card_id, suffix):
        if not re.fullmatch('[0-9a-f]{32}', card_id):
            raise HTTPException(400, 'invalid_card')
        path = self.root / (card_id + suffix)
        if path.resolve().parent != self.root.resolve():
            raise HTTPException(400, 'invalid_card')
        return path

    def current(self):
        try:
            pointer = self.backend.runtime / 'voices' / 'character' / 'active.json'
            value = json.loads(pointer.read_text(encoding='utf-8'))
            return value.get('card_id') if isinstance(value, dict) else None
        except (OSError, ValueError):
            return None

    def cards(self):
        values = []
        active = self.current()
        for file in sorted(self.root.glob('*.json'), key=lambda p: p.stat().st_mtime, reverse=True)[:100]:
            if not re.fullmatch('[0-9a-f]{32}', file.stem) or file.resolve().parent != self.root.resolve():
                continue
            try:
                value = json.loads(file.read_text(encoding='utf-8'))
                if not isinstance(value, dict):
                    continue
                value['active'] = file.stem == active
                values.append(value)
            except (OSError, ValueError):
                continue
        return values

    def generate(self, request):
        self.root.mkdir(parents=True, exist_ok=True)
        if len(list(self.root.glob('*.json'))) >= 200:
            raise HTTPException(409, 'candidate_storage_limit')
        if not request.text.strip():
            raise HTTPException(400, 'empty_input')
        audio, timing = self.backend.synthesize(self.speech_type(
            input=request.text.strip(), voice=request.voice, seed=request.seed,
            temperature=request.temperature, top_p=request.top_p))
        card_id = uuid.uuid4().hex
        value = dict(id=card_id, text=request.text.strip(), voice=request.voice, seed=request.seed,
                     temperature=request.temperature, top_p=request.top_p, timing=timing,
                     created_at=int(time.time()), audio_url='/lab/cards/'+card_id+'/audio', active=False)
        wav = self.card_path(card_id, '.wav')
        wav.write_bytes(audio)
        self.card_path(card_id, '.json').write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')
        return value

    def candidate_reference(self, card_id):
        import soundfile as sf
        audio = self.card_path(card_id, '.wav')
        metadata = self.card_path(card_id, '.json')
        if not audio.is_file() or not metadata.is_file():
            raise HTTPException(404, 'card_not_found')
        info = sf.info(str(audio))
        if not 3 <= info.duration <= 30 or info.channels not in (1, 2):
            raise HTTPException(400, 'reference_must_be_3_to_30_seconds')
        value = json.loads(metadata.read_text(encoding='utf-8'))
        voice_id = 'lab_'+card_id
        root = (self.backend.runtime / 'voices').resolve()
        target = root / voice_id
        if target.resolve().parent != root:
            raise HTTPException(400, 'invalid_voice')
        target.mkdir(exist_ok=True)
        for filename in ('reference.wav', 'reference.txt'):
            if (target / filename).resolve().parent != target.resolve():
                raise HTTPException(400, 'invalid_voice')
        # Immutable card references are copied once; preview never changes the active pointer.
        if not (target / 'reference.wav').exists():
            shutil.copyfile(audio, target / 'reference.wav')
        if not (target / 'reference.txt').exists():
            (target / 'reference.txt').write_text(value['text'], encoding='utf-8')
        return voice_id

    def preview(self, card_id, text):
        if not text.strip():
            raise HTTPException(400, 'empty_input')
        if not self.backend.lock.acquire(blocking=False):
            raise HTTPException(409, 'busy')
        try:
            voice_id = self.candidate_reference(card_id)
        finally:
            self.backend.lock.release()
        return self.backend.synthesize(self.speech_type(input=text.strip(), voice=voice_id))

    def select(self, card_id=None):
        if not self.backend.lock.acquire(blocking=False):
            raise HTTPException(409, 'busy')
        try:
            root = (self.backend.runtime / 'voices').resolve()
            if card_id is not None:
                voice_id = self.candidate_reference(card_id)
            else:
                voice_id = 'source_remote'
                self.backend.reference(voice_id)
            active = root / 'character'
            active.mkdir(exist_ok=True)
            if active.resolve().parent != root:
                raise HTTPException(400, 'invalid_voice')
            pointer = active / 'active.json'
            if pointer.exists():
                shutil.copyfile(pointer, active / 'active.previous.json')
            temporary = active / ('active.'+uuid.uuid4().hex+'.tmp')
            temporary.write_text(json.dumps(dict(voice=voice_id, card_id=card_id)), encoding='utf-8')
            temporary.replace(pointer)
            self.backend.prompts.pop('character', None)
            return dict(active_card=card_id, voice='character', original_preserved=True)
        finally:
            self.backend.lock.release()


def attach_voice_lab(app, backend, speech_type, failure_type):
    lab = VoiceLab(backend, speech_type, failure_type)

    @app.get('/lab/cards')
    def list_cards():
        return dict(cards=lab.cards(), active_card=lab.current())

    @app.post('/lab/cards')
    async def generate_card(request: CardRequest):
        try:
            return await asyncio.to_thread(lab.generate, request)
        except failure_type as error:
            code = str(error)
            raise HTTPException(429 if code == 'busy' else 400, code) from None
        except HTTPException:
            raise
        except Exception as error:
            print('Voice candidate failed:', type(error).__name__, flush=True)
            raise HTTPException(500, 'candidate_failed') from None

    @app.get('/lab/cards/{card_id}/audio')
    def card_audio(card_id: str):
        path = lab.card_path(card_id, '.wav')
        if not path.is_file():
            raise HTTPException(404, 'card_not_found')
        return FileResponse(path, media_type='audio/wav', headers={'Cache-Control':'no-store'})

    @app.get('/lab/original/audio')
    def original_audio():
        try:
            path, _, _ = backend.reference('source_remote')
            return FileResponse(path, media_type='audio/wav', headers={'Cache-Control':'no-store'})
        except failure_type as error:
            raise HTTPException(404, str(error)) from None

    @app.post('/lab/cards/{card_id}/preview')
    async def preview_card(card_id: str, request: PreviewRequest):
        try:
            audio, timing = await asyncio.to_thread(lab.preview, card_id, request.text)
            return Response(audio, media_type='audio/wav', headers={
                'Cache-Control':'no-store', 'X-Synthesis-Ms':str(timing['synthesis_ms']),
                'X-Audio-Ms':str(timing['audio_ms']), 'X-RTF':str(timing['rtf'])})
        except failure_type as error:
            raise HTTPException(429 if str(error) == 'busy' else 400, str(error)) from None
        except HTTPException:
            raise
        except Exception as error:
            print('Voice preview failed:', type(error).__name__, flush=True)
            raise HTTPException(500, 'synthesis_failed') from None

    @app.post('/lab/activate')
    async def activate(selection: Selection):
        try:
            return await asyncio.to_thread(lab.select, selection.id)
        except failure_type as error:
            raise HTTPException(400, str(error)) from None
        except HTTPException:
            raise
        except Exception as error:
            print('Voice selection failed:', type(error).__name__, flush=True)
            raise HTTPException(500, 'selection_failed') from None

    @app.post('/lab/restore')
    async def restore():
        try:
            return await asyncio.to_thread(lab.select)
        except failure_type as error:
            raise HTTPException(400, str(error)) from None
        except HTTPException:
            raise
        except Exception as error:
            print('Voice restore failed:', type(error).__name__, flush=True)
            raise HTTPException(500, 'selection_failed') from None
