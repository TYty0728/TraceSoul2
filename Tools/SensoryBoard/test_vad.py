import unittest
import threading
from types import SimpleNamespace
import numpy as np
from local_vad import Utterance
from local_speaker import LocalSpeaker


class Detector:
    def __init__(self):
        self.speaking = False
        self.segment = None
        self.accepted = []

    def accept_waveform(self, samples): self.accepted.append(samples)
    def is_speech_detected(self): return self.speaking
    def empty(self): return self.segment is None
    @property
    def front(self): return SimpleNamespace(samples=self.segment)
    def pop(self): self.segment = None


class VadChecks(unittest.TestCase):
    def test_silence_ends_without_an_utterance(self):
        vad = Utterance(Detector())
        for _ in range(250): vad.feed(bytes(3072))
        self.assertEqual(vad.reason, 'no_speech')
        self.assertEqual(vad.pcm, b'')
        self.assertEqual(len(vad.detector.accepted[0]), 512)

    def test_endpoint_retains_actual_segment(self):
        detector = Detector()
        detector.speaking = True
        vad = Utterance(detector)
        self.assertFalse(vad.feed(bytes(3072)))
        detector.segment = np.full(16000, 0.1)
        detector.speaking = False
        self.assertTrue(vad.feed(bytes(3072)))
        self.assertEqual(vad.reason, 'speech_end')
        self.assertEqual(len(vad.pcm), 32000)

    def test_overlong_speech_is_not_a_truncated_success(self):
        detector = Detector(); detector.speaking = True
        vad = Utterance(detector)
        for _ in range(377): vad.feed(bytes(3072))
        self.assertEqual(vad.reason, 'speech_too_long')
        self.assertEqual(vad.pcm, b'')

    def test_speaker_windows_include_the_utterance_tail(self):
        class Extractor:
            def create_stream(self): return self
            def accept_waveform(self, rate, samples): self.last = samples.copy()
            def input_finished(self): pass
            def is_ready(self, stream): return True
            def compute(self, stream):
                vector = [0.0] * 192
                vector[int(np.mean(self.last) > 0.01)] = 1.0
                return vector
        speaker = LocalSpeaker.__new__(LocalSpeaker)
        speaker.extractor = Extractor(); speaker.lock = threading.Lock()
        samples = np.concatenate((np.zeros(32000), np.full(32000, 2000))).astype('<i2')
        result = speaker.evidence(samples.tobytes())
        self.assertEqual(result['speech_ms'], 4000)
        self.assertEqual(result['windows'][0]['embedding'][0], 1)
        self.assertEqual(result['windows'][-1]['embedding'][1], 1)
        self.assertIsNone(speaker.evidence(bytes(32000)))
