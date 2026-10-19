import copy
import io
import json
import unittest
import urllib.error

from client import DecisionClient, DecisionError, NoRedirect, validate_answers


CONFIG = {
    'base_url': 'https://relay.example.com/tracesoul',
    'relay_key': 'test-relay-secret',
    'openrouter_api_key': 'test-upstream-secret',
    'model': 'typesafe/jev-1.14',
}
QUESTIONS = {'attention': {'type': 'choice', 'instructions': 'Select next action.',
                           'criteria': {'wait': 'No change', 'observe': 'Need information'}}}
RESULT = {'answers': {'attention': {'type': 'choice', 'choice': 'wait', 'confidence': 0.8,
                                    'probabilities': {'wait': 0.9, 'observe': 0.1}}}}


class FakeOpener:
    def __init__(self, result=RESULT, raw=None, error=None):
        self.raw = raw if raw is not None else json.dumps(result).encode('utf-8')
        self.error = error
        self.requests = []

    def open(self, request, timeout):
        self.requests.append(request)
        if self.error:
            raise self.error
        return io.BytesIO(self.raw)


class ClientTests(unittest.TestCase):
    def test_native_request_and_valid_result(self):
        opener = FakeOpener()
        result = DecisionClient(CONFIG, opener).decide({'scene': '安静'}, QUESTIONS)
        request = opener.requests[0]
        self.assertEqual(request.full_url, CONFIG['base_url'] + '/decisions')
        self.assertEqual(request.get_method(), 'POST')
        self.assertEqual(request.get_header('Authorization'), 'Bearer test-upstream-secret')
        self.assertEqual(request.get_header('X-tracesoul-relay-key'), 'test-relay-secret')
        self.assertEqual(json.loads(request.data), {'model': CONFIG['model'], 'state': {'scene': '安静'},
                                                   'questions': QUESTIONS})
        self.assertNotIn(b'secret', request.data)
        self.assertEqual(result, RESULT)

    def test_discovery_without_upstream_key(self):
        opener = FakeOpener({'data': [{'id': 'a', 'name': 'A'}, {'id': 'b'}, None]})
        result = DecisionClient({**CONFIG, 'openrouter_api_key': ''}, opener).models()
        self.assertEqual(result, [{'id': 'a', 'name': 'A'}, {'id': 'b', 'name': 'b'}])
        request = opener.requests[0]
        self.assertEqual(request.full_url, CONFIG['base_url'] + '/models')
        self.assertEqual(request.get_method(), 'GET')
        self.assertIsNone(request.get_header('Authorization'))

    def test_missing_upstream_key_blocks_inference(self):
        opener = FakeOpener()
        with self.assertRaises(DecisionError):
            DecisionClient({**CONFIG, 'openrouter_api_key': ''}, opener).decide({}, QUESTIONS)
        self.assertEqual(opener.requests, [])

    def test_alternative_model(self):
        opener = FakeOpener()
        DecisionClient(CONFIG, opener).decide({}, QUESTIONS, 'openai/gpt-6-luna-decisions')
        self.assertEqual(json.loads(opener.requests[0].data)['model'], 'openai/gpt-6-luna-decisions')

    def test_score_and_noul(self):
        questions = {'urgency': {'type': 'score', 'instructions': 'Rate.', 'criteria': ['Low', 'High']},
                     'changed': {'type': 'noul', 'instructions': 'Changed?'}}
        result = {'answers': {'urgency': {'type': 'score', 'score': 0.8, 'confidence': 0.7,
                                          'probabilities': {'0': 0.2, '1': 0.8}},
                              'changed': {'type': 'noul', 'noul': 0.6}}}
        self.assertEqual(DecisionClient(CONFIG, FakeOpener(result)).decide({}, questions), result)
        for bad in (True, -0.1, 1.1, float('nan')):
            with self.subTest(bad=bad):
                changed = copy.deepcopy(result)
                changed['answers']['changed']['noul'] = bad
                with self.assertRaises(DecisionError):
                    validate_answers(changed, questions)

    def test_invalid_answers_rejected(self):
        mutations = [
            {'choice': 'execute_shell'}, {'type': 'refusal'}, {'confidence': True},
            {'probabilities': {'wait': 1.2, 'observe': -0.2}},
            {'probabilities': {'wait': 0.1, 'observe': 0.1}},
            {'probabilities': {'unknown': 1}},
        ]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                result = copy.deepcopy(RESULT)
                result['answers']['attention'].update(mutation)
                with self.assertRaises(DecisionError):
                    DecisionClient(CONFIG, FakeOpener(result)).decide({}, QUESTIONS)
        for result in ({'answers': {}}, {'answers': {'extra': {}}}, [], {'error': 'refused'}):
            with self.subTest(result=result), self.assertRaises(DecisionError):
                validate_answers(result, QUESTIONS)

    def test_invalid_request_never_sent(self):
        for state, questions in [({}, {}), ({}, {'x': {'type': 'unknown', 'instructions': 'x'}}),
                                 ({'x': float('nan')}, QUESTIONS), ('x' * (256 * 1024), QUESTIONS)]:
            opener = FakeOpener()
            with self.subTest(state_type=type(state)), self.assertRaises(DecisionError):
                DecisionClient(CONFIG, opener).decide(state, questions)
            self.assertEqual(opener.requests, [])

    def test_errors_sanitized_and_not_retried(self):
        for status in (401, 429, 502):
            error = urllib.error.HTTPError(CONFIG['base_url'], status, 'test-upstream-secret', {},
                                           io.BytesIO(b'test-relay-secret'))
            opener = FakeOpener(error=error)
            with self.subTest(status=status), self.assertRaises(DecisionError) as raised:
                DecisionClient(CONFIG, opener).decide({}, QUESTIONS)
            self.assertIn(str(status), str(raised.exception))
            self.assertNotIn('secret', str(raised.exception))
            self.assertEqual(len(opener.requests), 1)
        opener = FakeOpener(error=urllib.error.URLError('test-upstream-secret'))
        with self.assertRaises(DecisionError) as raised:
            DecisionClient(CONFIG, opener).decide({}, QUESTIONS)
        self.assertNotIn('secret', str(raised.exception))
        self.assertEqual(len(opener.requests), 1)

    def test_malformed_or_oversized_response(self):
        for raw in (b'not-json', b'\xff', b'x' * (1024 * 1024 + 1)):
            with self.subTest(size=len(raw)), self.assertRaises(DecisionError):
                DecisionClient(CONFIG, FakeOpener(raw=raw)).decide({}, QUESTIONS)

    def test_invalid_connections(self):
        for url in ('http://relay.example.com', 'https://' + 'u:p@' + 'relay.example.com',
                    'https://relay.example.com?key=a', 'https://relay.example.com/#fragment',
                    'https://relay.example.com:bad', 'https://relay.example.com\n'):
            with self.subTest(url=url), self.assertRaises(DecisionError):
                DecisionClient({**CONFIG, 'base_url': url})
        for config in ([], {**CONFIG, 'relay_key': None}, {**CONFIG, 'relay_key': '   '},
                       {**CONFIG, 'relay_key': 'secret\n'}, {**CONFIG, 'openrouter_api_key': 'a\rb'}):
            with self.subTest(config_type=type(config)), self.assertRaises(DecisionError):
                DecisionClient(config)

    def test_redirects_disabled(self):
        self.assertIsNone(NoRedirect().redirect_request(None, None, 307, '', {}, 'https://other.example'))


if __name__ == '__main__':
    unittest.main()
