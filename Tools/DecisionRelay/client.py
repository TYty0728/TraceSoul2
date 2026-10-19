"""Small OpenRouter Decisions client for the private HTTPS relay; standard library only."""
import argparse
import json
import math
from pathlib import Path
import sys
import urllib.error
import urllib.parse
import urllib.request


class DecisionError(Exception):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def probability(value):
    return type(value) in (int, float) and math.isfinite(value) and 0 <= value <= 1


def validate_questions(questions):
    if not isinstance(questions, dict) or not 1 <= len(questions) <= 20:
        raise DecisionError('questions must contain 1 to 20 named questions.')
    for name, question in questions.items():
        if not isinstance(name, str) or not name or not isinstance(question, dict):
            raise DecisionError('Invalid question name or value.')
        if not isinstance(question.get('instructions'), str) or not question['instructions'].strip():
            raise DecisionError('Each question needs instructions.')
        kind = question.get('type')
        criteria = question.get('criteria')
        if kind == 'choice':
            if not isinstance(criteria, dict) or not 2 <= len(criteria) <= 20:
                raise DecisionError('choice requires 2 to 20 named criteria.')
            if any(not isinstance(k, str) or not k or (v is not None and not isinstance(v, str))
                   for k, v in criteria.items()):
                raise DecisionError('Invalid choice criteria.')
        elif kind == 'score':
            if not isinstance(criteria, list) or not 2 <= len(criteria) <= 20 or any(
                    not isinstance(v, str) or not v.strip() for v in criteria):
                raise DecisionError('score requires 2 to 20 ordered descriptions.')
        elif kind == 'noul':
            if criteria is not None and (not isinstance(criteria, dict) or
                    set(criteria) != {'true', 'false'} or any(not isinstance(v, str) for v in criteria.values())):
                raise DecisionError('noul criteria, if supplied, must describe true and false.')
        else:
            raise DecisionError('Supported question types: choice, noul, score.')


def validate_answers(result, questions):
    answers = result.get('answers') if isinstance(result, dict) else None
    if not isinstance(answers, dict) or set(answers) != set(questions):
        raise DecisionError('Decision response does not match the submitted question IDs.')
    for name, question in questions.items():
        answer = answers[name]
        kind = question['type']
        if not isinstance(answer, dict) or answer.get('type') != kind:
            raise DecisionError('Missing, refused or unexpected answer type.')
        if kind == 'noul':
            if not probability(answer.get('noul')):
                raise DecisionError('Invalid yes/no probability.')
            continue
        if not probability(answer.get('confidence')):
            raise DecisionError('Invalid confidence.')
        expected = set(question['criteria']) if kind == 'choice' else {str(i) for i in range(len(question['criteria']))}
        distribution = answer.get('probabilities')
        if not isinstance(distribution, dict) or set(distribution) != expected or any(
                not probability(v) for v in distribution.values()):
            raise DecisionError('Invalid probability distribution.')
        if abs(sum(distribution.values()) - 1) > 0.02:
            raise DecisionError('Probabilities do not sum to one.')
        if kind == 'choice' and (not isinstance(answer.get('choice'), str) or answer['choice'] not in expected):
            raise DecisionError('Decision selected an unknown choice.')
        if kind == 'score':
            value = answer.get('score')
            if type(value) not in (float, int) or not math.isfinite(value) or not 0 <= value <= len(expected) - 1:
                raise DecisionError('Score is outside the submitted rubric.')
    return result


class DecisionClient:
    def __init__(self, config, opener=None):
        if not isinstance(config, dict) or any(not isinstance(config.get(k, ''), str) for k in
                ('base_url', 'relay_key', 'openrouter_api_key', 'model')):
            raise DecisionError('Connection configuration must contain string fields.')
        self.base = config.get('base_url', '').rstrip('/')
        if any(c.isspace() or ord(c) < 32 for c in self.base):
            raise DecisionError('Invalid base_url.')
        try:
            url = urllib.parse.urlsplit(self.base)
            url.port
        except ValueError:
            raise DecisionError('Invalid base_url.') from None
        if url.scheme != 'https' or not url.hostname or url.username or url.password or url.query or url.fragment:
            raise DecisionError('base_url must be an HTTPS URL without credentials, query or fragment.')
        self.relay_key = config.get('relay_key', '')
        self.api_key = config.get('openrouter_api_key', '')
        if not self.relay_key or any(c in self.relay_key + self.api_key for c in '\r\n'):
            raise DecisionError('Missing or malformed credential.')
        self.relay_key = self.relay_key.strip()
        self.api_key = self.api_key.strip()
        if not self.relay_key or not (self.relay_key + self.api_key).isascii():
            raise DecisionError('Missing or malformed credential.')
        self.model = str(config.get('model', '')).strip()
        self.opener = opener or urllib.request.build_opener(NoRedirect())

    def _request(self, path, payload=None):
        headers = {'X-TraceSoul-Relay-Key': self.relay_key, 'Accept': 'application/json'}
        body = None
        if payload is not None:
            if not self.api_key:
                raise DecisionError('Set openrouter_api_key in the private connection file before inference.')
            try:
                body = json.dumps(payload, ensure_ascii=False, allow_nan=False).encode('utf-8')
            except (TypeError, ValueError):
                raise DecisionError('Request must contain valid JSON values.') from None
            if len(body) > 256 * 1024:
                raise DecisionError('Request exceeds the relay limit of 256 KiB.')
            headers.update({'Authorization': 'Bearer ' + self.api_key, 'Content-Type': 'application/json'})
        request = urllib.request.Request(self.base + path, data=body, headers=headers)
        try:
            with self.opener.open(request, timeout=60) as response:
                raw = response.read(1024 * 1024 + 1)
            if len(raw) > 1024 * 1024:
                raise DecisionError('Response exceeds 1 MiB.')
            return json.loads(raw)
        except urllib.error.HTTPError as error:
            status = error.code
            error.close()
            raise DecisionError('Relay/upstream returned HTTP ' + str(status) + '; no automatic retry.') from None
        except (urllib.error.URLError, TimeoutError, OSError):
            raise DecisionError('Connection or TLS failure; no automatic retry.') from None
        except (ValueError, UnicodeDecodeError):
            raise DecisionError('Upstream did not return valid JSON.') from None

    def models(self):
        result = self._request('/models')
        if not isinstance(result, dict) or not isinstance(result.get('data'), list):
            raise DecisionError('Invalid model list response.')
        return [{'id': item['id'], 'name': item.get('name', item['id'])}
                for item in result['data'] if isinstance(item, dict) and isinstance(item.get('id'), str)]

    def decide(self, state, questions, model=None):
        validate_questions(questions)
        selected = model or self.model
        if not isinstance(selected, str) or not selected.strip():
            raise DecisionError('Select a decision model.')
        result = self._request('/decisions', {'model': selected, 'state': state, 'questions': questions})
        return validate_answers(result, questions)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, help='Private connection JSON, outside the repository')
    sub = parser.add_subparsers(dest='command', required=True)
    sub.add_parser('models', help='Fetch models without a paid inference request')
    decide = sub.add_parser('decide', help='Send one paid decision request; does not execute the decision')
    decide.add_argument('--state', required=True, help='UTF-8 JSON state file')
    decide.add_argument('--questions', required=True, help='UTF-8 JSON questions file')
    decide.add_argument('--model', help='Override the model from the connection file')
    args = parser.parse_args()
    try:
        client = DecisionClient(json.loads(Path(args.config).read_text(encoding='utf-8-sig')))
        result = client.models() if args.command == 'models' else client.decide(
            json.loads(Path(args.state).read_text(encoding='utf-8-sig')),
            json.loads(Path(args.questions).read_text(encoding='utf-8-sig')), args.model)
        print(json.dumps(result, ensure_ascii=False, indent=2))
    except DecisionError as error:
        parser.exit(1, str(error) + '\n')
    except (OSError, ValueError):
        parser.exit(1, 'Cannot read a valid configuration or input JSON file.\n')


if __name__ == '__main__':
    main()
