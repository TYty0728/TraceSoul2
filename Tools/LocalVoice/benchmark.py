"""Measure complete-response latency against the local service, saving public test phrases."""
import argparse
import io
import json
from pathlib import Path
import time
import urllib.request
import wave
from access import load_key,private_opener


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--port", type=int, default=5088)
    parser.add_argument("--voice", default="demo")
    parser.add_argument("--runtime",type=Path,required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    reports = []
    key=load_key(args.runtime)
    for name, text in [("first", "你好，我在这里。"), ("warm", "你好，我在这里。"),
                       ("sentence", "我在呢，你先忙你的，想说话的时候叫我就好。")]:
        request = urllib.request.Request(f"http://127.0.0.1:{args.port}/v1/audio/speech",
            data=json.dumps({"input": text, "voice": args.voice}).encode(),
            headers={"Content-Type": "application/json","Authorization":"Bearer "+key})
        started = time.perf_counter()
        with private_opener().open(request, timeout=180) as response:
            audio = response.read()
            synthesis_ms = int(response.headers["X-Synthesis-Ms"])
        elapsed = time.perf_counter() - started
        with wave.open(io.BytesIO(audio)) as wav:
            duration = wav.getnframes() / wav.getframerate()
            assert wav.getnchannels() == 1 and wav.getsampwidth() == 2 and duration > 0
        (args.output / f"{name}.wav").write_bytes(audio)
        report = {"case": name, "text": text, "voice": args.voice,
                  "response_ms": round(elapsed * 1000), "synthesis_ms": synthesis_ms,
                  "audio_ms": round(duration * 1000), "rtf": round(synthesis_ms / (duration * 1000), 3)}
        reports.append(report)
        print(json.dumps(report, ensure_ascii=True), flush=True)
    (args.output / "benchmark.json").write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
