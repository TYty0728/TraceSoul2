"""Optional, local-only SenseVoice recognizer. Raw recordings stay in RAM."""
import re
import threading
import time
from pathlib import Path


class LocalAsr:
    def __init__(self, directory):
        import sherpa_onnx
        directory = Path(directory)
        self.recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=str(directory / 'model.int8.onnx'), tokens=str(directory / 'tokens.txt'),
            num_threads=4, use_itn=True, language='zh', debug=False, provider='cpu')
        self.lock = threading.Lock()

    def transcribe(self, pcm, sample_rate=48000):
        import numpy as np
        if sample_rate not in (16000, 48000) or not pcm or len(pcm) % 2 or len(pcm) > sample_rate * 2 * 20:
            raise ValueError('无效的麦克风音频规格')
        samples = np.frombuffer(pcm, dtype='<i2').astype(np.float32)
        rms = float(np.sqrt(np.mean(samples * samples)))
        result = {'text': '', 'rms': round(rms, 2), 'peak': int(np.max(np.abs(samples))),
                  'audio_ms': len(pcm) * 1000 // (sample_rate * 2), 'model': 'SenseVoice int8 (local CPU)'}
        if rms < 60:
            result.update(status='quiet', recognition_ms=0)
            return result
        start = time.monotonic()
        with self.lock:
            stream = self.recognizer.create_stream()
            stream.accept_waveform(sample_rate, samples / 32768.0)
            self.recognizer.decode_stream(stream)
            text = re.sub(r'<\|[^|]*\|>', '', stream.result.text).strip()
        if len(text) > 4000:
            raise ValueError('听写结果超长，未提交')
        # Noise can decode to punctuation alone; it is not a spoken utterance.
        if not any(character.isalnum() for character in text):
            text = ''
        result.update(text=text, status='transcribed' if text else 'no_text',
                      recognition_ms=round((time.monotonic() - start) * 1000))
        return result
