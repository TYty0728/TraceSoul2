"""Local speaker features only; the Soul Host owns enrollment and admission."""
import threading
import hashlib
from pathlib import Path

MODEL = '3dspeaker/campplus-zh-cn-16k:f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11'


class LocalSpeaker:
    def __init__(self, model):
        import sherpa_onnx
        if hashlib.sha256(Path(model).read_bytes()).hexdigest() != MODEL.rsplit(':', 1)[1]:
            raise ValueError('声纹模型校验失败')
        config = sherpa_onnx.SpeakerEmbeddingExtractorConfig(
            model=str(model), num_threads=2, debug=False, provider='cpu')
        self.extractor = sherpa_onnx.SpeakerEmbeddingExtractor(config)
        if self.extractor.dim != 192:
            raise ValueError('声纹模型维度不匹配')
        self.lock = threading.Lock()

    def evidence(self, pcm, rate=16000):
        import numpy as np
        if rate != 16000 or len(pcm) % 2 or not 38400 <= len(pcm) <= rate * 2 * 20:
            return None
        samples = np.frombuffer(pcm, dtype='<i2').astype(np.float32) / 32768.0
        width = min(len(samples), 32000)
        # Cover the utterance and tail for short-window diagnostics. These scores
        # alone are not a speaker-segmentation or diarization result.
        starts = np.linspace(0, len(samples) - width,
                             max(1, int(np.ceil(len(samples) / width)))).astype(int)
        windows = []
        with self.lock:
            def encode(chunk):
                stream = self.extractor.create_stream()
                stream.accept_waveform(rate, chunk)
                stream.input_finished()
                if not self.extractor.is_ready(stream):
                    return None
                vector = np.asarray(self.extractor.compute(stream), dtype=np.float64)
                norm = np.linalg.norm(vector)
                if vector.shape != (192,) or not np.isfinite(vector).all() or norm < 1e-8:
                    return None
                return vector / norm
            whole = encode(samples)
            if whole is None:
                return None
            for start in starts:
                vector = encode(samples[start:start + width])
                if vector is None:
                    return None
                windows.append({'speech_ms': width * 1000 // rate,
                                'embedding': vector.tolist()})
        return {'model': MODEL, 'speech_ms': len(samples) * 1000 // rate,
                'embedding': whole.tolist(), 'windows': windows}
