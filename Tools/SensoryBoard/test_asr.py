import unittest
try:
    import numpy as np
except ImportError:
    np = None
from local_asr import LocalAsr


class Recognizer:
    class Result:
        text = '<|zh|><|NEUTRAL|><|Speech|><|woitn|>测试语音。'

    def create_stream(self):
        return self

    def accept_waveform(self, rate, samples):
        assert rate in (16000, 48000) and np.max(np.abs(samples)) <= 1

    def decode_stream(self, stream):
        pass

    result = Result()


@unittest.skipIf(np is None, 'Optional ASR dependencies are not installed')
class AsrChecks(unittest.TestCase):
    def setUp(self):
        import threading
        self.asr = LocalAsr.__new__(LocalAsr)
        self.asr.recognizer = Recognizer()
        self.asr.lock = threading.Lock()

    def test_silence_does_not_generate_hallucinated_text(self):
        result = self.asr.transcribe(b'\0' * 96000)
        self.assertEqual(result['text'], '')
        self.assertEqual(result['status'], 'quiet')

    def test_real_samples_normalize_and_strip_model_tags(self):
        pcm = np.full(48000, 1000, dtype='<i2').tobytes()
        self.assertEqual(self.asr.transcribe(pcm)['text'], '测试语音。')

    def test_punctuation_only_noise_is_not_an_utterance(self):
        self.asr.recognizer.result = type('Result', (), {'text': '<|zh|>。！？…'})()
        pcm = np.full(48000, 200, dtype='<i2').tobytes()
        result = self.asr.transcribe(pcm)
        self.assertEqual(result['text'], '')
        self.assertEqual(result['status'], 'no_text')

    def test_invalid_pcm_rejected_before_recognizer(self):
        for pcm in (b'', b'x', b'\0' * 1920002):
            with self.assertRaises(ValueError):
                self.asr.transcribe(pcm)
