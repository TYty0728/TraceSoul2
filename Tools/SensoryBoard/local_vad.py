"""One bounded utterance from 48 kHz board chunks. No text or audio is persisted."""
from pathlib import Path


class LocalVad:
    def __init__(self, model):
        import sherpa_onnx
        self.config = sherpa_onnx.VadModelConfig()
        self.config.silero_vad.model = str(Path(model))
        self.config.silero_vad.threshold = 0.5
        self.config.silero_vad.min_speech_duration = 0.25
        # Detect small voiced regions; Utterance joins them into one speaking turn.
        self.config.silero_vad.min_silence_duration = 0.35
        self.config.silero_vad.max_speech_duration = 20
        self.config.sample_rate = 16000
        self.config.num_threads = 1
        # Fail before opening the microphone if the model cannot be loaded.
        self.create()

    def create(self, join_pause_ms=1280):
        import sherpa_onnx
        return Utterance(sherpa_onnx.VoiceActivityDetector(self.config, buffer_size_in_seconds=25), join_pause_ms)

    def voiced(self, pcm):
        return self.analyze(pcm)[0]

    def analyze(self, pcm):
        """Finite enrollment capture: gather all detected speech, not raw room audio."""
        # Enrollment gathers individual voiced regions in its fixed window.
        session = self.create(join_pause_ms=0)
        speech = bytearray()
        padded = pcm + bytes(48000 * 2 * 2)
        for offset in range(0, len(padded), 3072):
            session.feed(padded[offset:offset + 3072].ljust(3072, b'\0'))
            if session.pcm:
                speech.extend(session.pcm)
                session.pcm = b''
        return bytes(speech), session.segments


class Utterance:
    def __init__(self, detector, join_pause_ms=1280):
        import numpy as np
        self.detector = detector
        # Causal low-pass filter before 48k -> 16k decimation. Each 32ms chunk
        # yields exactly one 512-sample Silero window; filter state crosses chunks.
        axis = np.arange(63, dtype=np.float32) - 31
        taps = np.sinc(0.3 * axis) * np.hamming(63)
        self.taps = (taps / taps.sum()).astype(np.float32)
        self.tail = np.zeros(62, dtype=np.float32)
        self.frames = 0
        self.started_at = None
        self.reason = None
        self.pcm = b''
        self.recognition_pcm = b''
        self.context_pcm = bytearray()
        self.segments = []
        self.join_pause_frames = (join_pause_ms + 31) // 32
        self.pending_pcm = bytearray()
        self.last_segment_frame = None

    def feed(self, pcm):
        import numpy as np
        if len(pcm) != 3072:
            raise ValueError('语音检测需要 32ms PCM16/48kHz 数据')
        samples = np.frombuffer(pcm, dtype='<i2').astype(np.float32) / 32768.0
        history = np.concatenate((self.tail, samples))
        filtered = np.convolve(history, self.taps, mode='valid')[::3]
        if len(self.context_pcm) + len(filtered) * 2 > 25 * 16000 * 2:
            raise ValueError('语音上下文超过25秒上限')
        self.context_pcm.extend((np.clip(filtered, -1, 1) * 32767).astype('<i2').tobytes())
        self.tail = history[-62:].copy()
        self.frames += 1
        self.detector.accept_waveform(filtered)
        speaking = self.detector.is_speech_detected()
        if self.started_at is None and speaking:
            self.started_at = self.frames
        if speaking and self.last_segment_frame is not None:
            self.last_segment_frame = self.frames
        while not self.detector.empty():
            segment = self.detector.front
            speech = np.asarray(segment.samples, dtype=np.float32).copy()
            start = max(0, int(getattr(segment, 'start', self.frames * 512 - len(speech))))
            self.detector.pop()
            self.pending_pcm.extend((np.clip(speech, -1, 1) * 32767).astype('<i2').tobytes())
            if self.started_at is None:
                self.started_at = max(0, start // 512)
            self.last_segment_frame = self.frames
            self.segments.append({'start_ms': start * 1000 // 16000, 'duration_ms': len(speech) * 1000 // 16000})
        if self.pending_pcm and not speaking and self.frames - self.last_segment_frame >= self.join_pause_frames:
            self.pcm = bytes(self.pending_pcm)
            self.pending_pcm.clear()
            # ASR receives the entire capture, including quiet leading words and
            # clause pauses. VAD regions gate a turn; they never crop its words.
            self.recognition_pcm = bytes(self.context_pcm)
            self.reason = 'speech_end'
        elif self.started_at is None and self.frames * 32 >= 8000:
            self.reason = 'no_speech'
        elif self.started_at is not None and (self.frames - self.started_at) * 32 >= 12000:
            # Do not submit a silently truncated sentence.
            self.reason = 'speech_too_long'
        return self.reason is not None
