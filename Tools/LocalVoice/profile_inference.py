"""Offline component timings against an already installed model; no board or cloud calls."""
import argparse
import json
from pathlib import Path
import time
from concurrent.futures import ThreadPoolExecutor

from server import LocalVoice, SpeechRequest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--threads', type=int, default=4, choices=(1, 2, 4))
    parser.add_argument('--profiler', action='store_true')
    parser.add_argument('--worker', action='store_true')
    parser.add_argument('--predictor-graph', action='store_true')
    args = parser.parse_args()
    import torch
    backend = LocalVoice(args.runtime, accelerate=args.predictor_graph)
    backend.load()
    torch.set_num_threads(args.threads)
    current = {}

    def timed(target, name, label):
        original = getattr(target, name)
        def wrapper(*values, **options):
            torch.cuda.synchronize()
            start = time.perf_counter()
            result = original(*values, **options)
            torch.cuda.synchronize()
            current[label] = round((time.perf_counter()-start)*1000)
            if label == 'tokens_ms':
                current['audio_frames'] = int(result[0][0].shape[0])
            return result
        setattr(target, name, wrapper)

    timed(backend.model, 'create_voice_clone_prompt', 'reference_ms')
    timed(backend.model.model, 'generate', 'tokens_ms')
    timed(backend.model.model.speech_tokenizer, 'decode', 'decode_ms')
    reports = []
    args.output.mkdir(parents=True, exist_ok=True)
    def synthesize():
        print(json.dumps(dict(worker=args.worker,torch_threads=torch.get_num_threads())),flush=True)
        return backend.synthesize(SpeechRequest(input='我在呢。', seed=20261017, max_new_tokens=96))
    pool = ThreadPoolExecutor(max_workers=1) if args.worker else None
    for index in range(2):
        current.clear()
        audio, timing = pool.submit(synthesize).result() if pool else synthesize()
        report = dict(case='cold' if index == 0 else 'warm', threads=args.threads,
                      acceleration=backend.acceleration,
                      **current, **timing)
        reports.append(report)
        (args.output/f'{args.threads}threads-{index}.wav').write_bytes(audio)
        print(json.dumps(report), flush=True)
    if args.profiler:
        with torch.profiler.profile(activities=[torch.profiler.ProfilerActivity.CPU,
                                                torch.profiler.ProfilerActivity.CUDA]) as prof:
            backend.synthesize(SpeechRequest(input='我在呢。',seed=20261017,max_new_tokens=96))
        (args.output/'operators.txt').write_text(prof.key_averages().table(
            sort_by='self_cpu_time_total', row_limit=25), encoding='utf-8')
    if pool:
        pool.shutdown()
    (args.output/f'{args.threads}threads.json').write_text(json.dumps(reports,indent=2),encoding='utf-8')


if __name__ == '__main__':
    main()
