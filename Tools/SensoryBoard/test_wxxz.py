import json
import threading
import unittest
import urllib.error
import urllib.request
from unittest.mock import patch
from http.server import ThreadingHTTPServer

import bridge


class SpeakChecks(unittest.TestCase):
    def check_request(self, pcm, fail=False):
        class Board:
            calls = []

            def transact(self, op, data):
                self.calls.append((op, len(data)))
                if fail:
                    raise TimeoutError('unknown')
                return dict(protocol=1, status='done', received=len(data))

        board = Board()
        server = ThreadingHTTPServer(('127.0.0.1', 0), bridge.make_handler(board, 'unused'))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with patch.object(bridge, 'system_speech', return_value=pcm):
                req = urllib.request.Request(f'http://127.0.0.1:{server.server_port}/api/speak',
                                             data=b'{"text":"test"}', headers={'Content-Type': 'application/json'})
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
                try:
                    response = opener.open(req)
                except urllib.error.HTTPError as error:
                    response = error
                return response.status, json.load(response), board.calls
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_long_speech_is_one_continuous_playback(self):
        code, data, calls = self.check_request(b'\0' * (8 * 96000 + 48000))
        self.assertEqual(code, 200)
        self.assertEqual(calls, [('audio_stream', 816000)])
        self.assertEqual(len(data['receipts']), 1)

    def test_unknown_playback_is_not_repeated(self):
        code, data, calls = self.check_request(b'\0' * 96000, fail=True)
        self.assertEqual(code, 503)
        self.assertFalse(data['ok'])
        self.assertEqual(len(calls), 1)

    def test_speech_rejects_empty_or_oversized_text(self):
        for text in ('', 'x' * 501, None):
            with self.assertRaises(ValueError):
                bridge.system_speech(text)


if __name__ == '__main__':
    unittest.main()
