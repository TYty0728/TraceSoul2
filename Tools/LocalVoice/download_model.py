"""Explicit setup step; synthesis itself uses only downloaded local assets."""
import argparse
import json
from pathlib import Path
import urllib.request

MODEL_ID = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
REVISION = "5d83992436eae1d760afd27aff78a71d676296fc"
DEMO_URL = "https://qianwen-res.oss-cn-beijing.aliyuncs.com/Qwen3-TTS-Repo/clone.wav"
DEMO_TEXT = "Okay. Yeah. I resent you. I love you. I respect you. But you know what? You blew it! And thanks to you."


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    args = parser.parse_args()
    from huggingface_hub import snapshot_download
    runtime = args.runtime.resolve()
    destination = runtime / "models" / MODEL_ID.split("/")[1]
    snapshot_download(MODEL_ID, revision=REVISION, local_dir=str(destination), token=False,
                      allow_patterns=["*.json", "*.safetensors", "*.txt", "README.md"], max_workers=3)
    demo = runtime / "voices" / "demo"
    demo.mkdir(parents=True, exist_ok=True)
    if not (demo / "reference.wav").exists():
        with urllib.request.urlopen(DEMO_URL, timeout=60) as response:
            audio = response.read(12 * 1024 * 1024 + 1)
        if len(audio) > 12 * 1024 * 1024 or audio[:4] != b"RIFF":
            raise RuntimeError("invalid_official_sample")
        (demo / "reference.wav").write_bytes(audio)
        (demo / "reference.txt").write_text(DEMO_TEXT, encoding="utf-8")
    (runtime / "voices" / "character").mkdir(parents=True, exist_ok=True)
    (runtime / "model-source.json").write_text(json.dumps({
        "model": MODEL_ID, "revision": REVISION, "demo_source": DEMO_URL,
        "demo_is_character_voice": False}, indent=2), encoding="utf-8")
    print("Local model and official validation sample prepared.")


if __name__ == "__main__":
    main()
