"""Real-model parity and timing check. Uses fixed public phrases, no microphone or cloud."""
import argparse
import hashlib
import json
from pathlib import Path

from server import LocalVoice, SpeechRequest
from predictor_graph import enable_predictor_graph


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    backend = LocalVoice(args.runtime, accelerate=False)
    backend.load()
    cases = [SpeechRequest(input='我在呢。',seed=17),
             SpeechRequest(input='我在呢。今天也想听你说说，刚才发生了什么。',seed=18),
             SpeechRequest(input='你好呀，今天也很高兴见到你。',seed=19),
             SpeechRequest(input='我在呢。',seed=20,temperature=0.7,top_p=0.9)]
    originals, report = [], []
    # Pre-fill reference features; cold and warm costs are measured separately by the profiler.
    backend.synthesize(cases[0])
    for index, request in enumerate(cases):
        audio,timing = backend.synthesize(request)
        originals.append((audio,timing))
        (args.output/f'original-{index}.wav').write_bytes(audio)
        print(json.dumps(dict(mode='original',case=index,**timing)),flush=True)
    assert enable_predictor_graph(backend.model) == 'predictor_cuda_graph'
    for index, request in enumerate(cases):
        audio,timing = backend.synthesize(request)
        old_audio,old_timing = originals[index]
        same = audio == old_audio
        item = dict(case=index,seed=request.seed,temperature=request.temperature,
                    original=old_timing,optimized=timing,wav_identical=same,
                    sha256=hashlib.sha256(audio).hexdigest())
        report.append(item)
        (args.output/f'optimized-{index}.wav').write_bytes(audio)
        print(json.dumps(item),flush=True)
    (args.output/'parity.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    assert all(item['wav_identical'] for item in report),'Audio parity failed; do not enable acceleration.'


if __name__ == '__main__':
    main()
