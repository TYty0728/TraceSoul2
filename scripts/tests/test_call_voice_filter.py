import asyncio
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[2] / "ExternalPlugins/RealtimeCall/gateway/voice_filter.py"
spec = importlib.util.spec_from_file_location("voice_filter", MODULE)
voice = importlib.util.module_from_spec(spec)
spec.loader.exec_module(voice)


class Vad:
    def is_speech(self, frame, rate):
        return True


def vector(index=0):
    result = [0.0] * 192
    result[index] = 1.0
    return result


def pcm(ms):
    return b"\x00\x10" * (16 * ms)


class VoiceFilterChecks(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.profile = Path(self.directory.name) / "voiceprint.json"
        self.encoded = []
        def encode(audio):
            self.encoded.append(len(audio))
            return vector()
        self.filter = voice.VoiceFilter(encode, vad_factory=Vad)
        self.filter.enroll([pcm(6000)] * 3, self.profile)
        self.encoded.clear()

    def tearDown(self):
        self.directory.cleanup()

    def test_enrollment_only_stores_features(self):
        data = json.loads(self.profile.read_text())
        self.assertEqual(data["sample_count"], 3)
        self.assertEqual(data["speech_ms"], 18000)
        self.assertEqual(set(data), {"model", "sample_count", "speech_ms", "embedding"})
        with self.assertRaises(FileExistsError):
            self.filter.enroll([pcm(6000)] * 3, self.profile)
        self.filter.enroll([pcm(6000)] * 3, self.profile, replace=True)
        self.assertEqual(list(self.profile.parent.iterdir()), [self.profile])

    def test_silence_never_calls_model_or_asr(self):
        def asr(_):
            self.fail("ASR called on silence")
        result, reason = self.filter.prepare_transcript(bytes(64000), self.profile, asr)
        self.assertIsNone(result)
        self.assertEqual(reason, "insufficient_speech")
        self.assertFalse(self.encoded)

    def test_echo_never_calls_model_or_asr(self):
        result, reason = self.filter.prepare_transcript(pcm(2000), self.profile,
                                                       lambda _: self.fail("ASR called"), True)
        self.assertIsNone(result)
        self.assertEqual(reason, "playback_echo_guard")
        self.assertFalse(self.encoded)

    def test_tv_not_transcribed(self):
        self.filter.encoder = lambda _: vector(1)
        result, reason = self.filter.prepare_transcript(pcm(2000), self.profile,
                                                       lambda _: self.fail("ASR called"))
        self.assertIsNone(result)
        self.assertEqual(reason, "speaker_mismatch")

    def test_mixed_speakers_rejected_including_tail(self):
        calls = iter([vector(), vector(), vector(1)])
        self.filter.encoder = lambda _: next(calls)
        result, reason, _ = self.filter.verify(pcm(5200), self.profile)
        self.assertIsNone(result)
        self.assertEqual(reason, "speaker_mismatch")

    def test_all_audio_covered_and_payload_bounded(self):
        result, reason = self.filter.prepare_transcript(pcm(10000), self.profile, lambda _: "你好" * 2000)
        self.assertIsNone(reason)
        self.assertEqual(sum(self.encoded), len(pcm(10000)))
        self.assertEqual(len(result["voice"]["windows"]), 4)
        self.assertLess(len(json.dumps(result).encode()), 64 * 1024 - 1024)

    def test_short_speech_and_invalid_pcm(self):
        self.assertEqual(self.filter.verify(pcm(780), self.profile)[1], "insufficient_speech")
        for bad in (b"a", pcm(10020)):
            with self.assertRaises(voice.VoiceError):
                self.filter.verify(bad, self.profile)

    def test_contaminated_enrollment_and_insufficient_samples(self):
        calls = iter([vector(), vector(), vector(1)] * 3)
        self.filter.encoder = lambda _: next(calls)
        with self.assertRaisesRegex(voice.VoiceError, "inconsistent_enrollment"):
            self.filter.enroll([pcm(6000)] * 3, self.profile, replace=True)
        self.assertEqual(voice.load_profile(self.profile), vector())
        with self.assertRaises(voice.VoiceError):
            self.filter.enroll([pcm(6000)], self.profile, replace=True)

    def test_profile_removal_and_corruption_fail_closed(self):
        self.profile.unlink()
        with self.assertRaisesRegex(voice.VoiceError, "voiceprint_not_enrolled"):
            self.filter.verify(pcm(2000), self.profile)
        self.profile.write_text('{"embedding": [NaN]}')
        with self.assertRaises(voice.VoiceError):
            self.filter.verify(pcm(2000), self.profile)

    def test_nonfinite_vector_and_threshold(self):
        for value in (float("nan"), float("inf"), 0):
            with self.assertRaises(voice.VoiceError):
                voice.normalize([value] * 192)
        with self.assertRaises(voice.VoiceError):
            voice.VoiceFilter(lambda _: vector(), threshold=float("nan"))


class KeepaliveChecks(unittest.IsolatedAsyncioTestCase):
    async def test_keepalive_independent_of_speech(self):
        stopped = asyncio.Event()
        sent = []
        async def request(message):
            sent.append(message)
            if len(sent) == 3:
                stopped.set()
        await asyncio.wait_for(voice.keepalive(request, stopped, interval=0.01), 1)
        self.assertEqual(sent, [{"op": "ping"}] * 3)

    async def test_connection_failure_propagates(self):
        async def request(_):
            raise ConnectionError("closed")
        with self.assertRaises(ConnectionError):
            await voice.keepalive(request, asyncio.Event(), interval=0.01)


if __name__ == "__main__":
    unittest.main()
