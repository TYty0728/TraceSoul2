import json
import unittest

import bridge


class RecordingLink:
    def __init__(self, wrong_checksum=False, wrong_size=False):
        self.pcm = b'\x10\x00' * 48000
        self.wrong_checksum, self.wrong_size = wrong_checksum, wrong_size
        self.lines = []
        self.offset = 0
        self.closed = False
        self.acks = []

    def write(self, data):
        value = json.loads(data)
        if value.get('op') == 'record':
            self.id = value['id']
            self.lines.extend([dict(id=self.id, protocol=1, status='ready'),
                               dict(id=self.id, protocol=1, status='captured', sample_rate=48000,
                                    size=len(self.pcm) + self.wrong_size,
                                    checksum=bridge.checksum(self.pcm) + self.wrong_checksum)])
        else:
            assert value['id'] == self.id and value['ack'] == self.offset
            self.acks.append(value['ack'])
            if self.offset == len(self.pcm):
                self.lines.append(dict(id=self.id, protocol=1, status='done', received=self.offset))

    def read_until(self, *_):
        return json.dumps(self.lines.pop(0)).encode() + b'\n'

    def read(self, size):
        chunk = self.pcm[self.offset:self.offset + size]
        self.offset += len(chunk)
        return chunk

    def close(self):
        self.closed = True


class RecordChecks(unittest.TestCase):
    def test_capture_is_framed_checked_and_acknowledged(self):
        board = bridge.Board('fake')
        board.connection = link = RecordingLink()
        pcm, receipt = board.record(1000)
        self.assertEqual(pcm, link.pcm)
        self.assertEqual(receipt['status'], 'done')
        self.assertEqual(link.acks[-1], 96000)

    def test_bad_checksum_never_returns_audio(self):
        board = bridge.Board('fake')
        board.connection = link = RecordingLink(wrong_checksum=True)
        with self.assertRaisesRegex(RuntimeError, '校验'):
            board.record(1000)
        self.assertTrue(link.closed)

    def test_wrong_size_is_rejected_before_reading(self):
        board = bridge.Board('fake')
        board.connection = link = RecordingLink(wrong_size=True)
        with self.assertRaises(RuntimeError):
            board.record(1000)
        self.assertEqual(link.offset, 0)

    def test_recording_duration_is_bounded(self):
        board = bridge.Board('fake')
        for duration in (0, 10001, True, '8000', 1.5):
            with self.assertRaises(ValueError):
                board.record(duration)


if __name__ == '__main__':
    unittest.main()
