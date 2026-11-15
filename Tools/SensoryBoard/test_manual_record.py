import json
import unittest
import bridge
from test_record import RecordingLink


class ManualLink(RecordingLink):
    def write(self, data):
        value = json.loads(data)
        if value.get('op') == 'manual_stop':
            self.recording_id = value['recording_id']
            value['op'] = 'record'
            return super().write(json.dumps(value).encode())
        if value.get('op') in ('manual_start', 'manual_cancel'):
            self.lines.append(dict(id=value['id'], protocol=1, status='ready'))
            return
        return super().write(data)


class ManualChecks(unittest.TestCase):
    def test_start_has_no_audio_transfer_and_stop_returns_complete_checked_audio(self):
        board = bridge.Board('unused')
        board.connection = link = ManualLink()
        pcm, _ = board.manual_record('start', 'a' * 32)
        self.assertIsNone(pcm)
        self.assertEqual(link.offset, 0)
        pcm, receipt = board.manual_record('stop', 'a' * 32)
        self.assertEqual(pcm, link.pcm)
        self.assertEqual(receipt['received'], len(pcm))
        self.assertEqual(link.recording_id, 'a' * 32)

    def test_corrupted_recording_never_returns_audio(self):
        board = bridge.Board('unused')
        board.connection = ManualLink(wrong_checksum=True)
        with self.assertRaisesRegex(RuntimeError, '校验'):
            board.manual_record('stop', 'a' * 32)

    def test_invalid_id_and_cancel_never_read_audio(self):
        board = bridge.Board('unused')
        board.connection = link = ManualLink()
        with self.assertRaises(ValueError):
            board.manual_record('stop', 'bad')
        self.assertTrue(link.closed)
        board.connection = link = ManualLink()
        self.assertEqual(board.manual_record('cancel', 'a' * 32)[0], None)
        self.assertEqual(link.offset, 0)


if __name__ == '__main__':
    unittest.main()
