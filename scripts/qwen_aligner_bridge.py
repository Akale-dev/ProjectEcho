"""ProjectEcho offline official Qwen ForcedAligner CPU bridge, batch-v2.

This optional postprocessing program loads only the official ForcedAligner checkpoint.
It preserves raw acoustic predictions. Official timestamp repairs are diagnostic sidecars,
never the word timestamps consumed by ProjectEcho. No network service or download is used.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import time
import unicodedata
import uuid
import wave

VERSION = "ProjectEcho-official-Qwen-ForcedAligner-python-v1"
PROTOCOL = "batch-v2"
RAW_MARKER = "projectecho-alignment: raw-model-boundaries-v1; sample_rate=16000; timestamp_quantum_ms=80"
MAXIMUM_REQUESTS = 16
MAXIMUM_JSON_BYTES = 8 * 1024 * 1024
MAXIMUM_TEXT_CHARACTERS = 6000
MAXIMUM_WORDS = 2048
MAXIMUM_AUDIO_FRAMES = 180 * 16000


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("Duplicate JSON property is prohibited.")
        value[key] = item
    return value


def local_path(value: str, *, exists: bool = True) -> Path:
    if not isinstance(value, str) or not value or "\0" in value or value.startswith(("\\\\", "//")):
        raise ValueError("A local filesystem path is required.")
    path = Path(value).absolute()
    if not path.drive or ":" in str(path)[len(path.anchor):]:
        raise ValueError("Network paths and alternate data streams are prohibited.")
    if os.name == "nt" and ctypes.windll.kernel32.GetDriveTypeW(str(path.anchor)) == 4:
        raise ValueError("Mapped network drives are prohibited.")
    for current in (path, *path.parents):
        if current.exists() and getattr(current.stat(follow_symlinks=False), "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked/reparse paths are prohibited.")
    if exists and not path.exists():
        raise FileNotFoundError(str(path))
    return path


def read_json(path: Path):
    if path.stat().st_size > MAXIMUM_JSON_BYTES:
        raise ValueError("JSON input exceeds the bounded local size limit.")
    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)


def hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def atomic_json(path: Path, value) -> None:
    local_path(str(path), exists=False)
    if path.exists():
        raise FileExistsError("A prior output is never overwritten: " + str(path))
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".partial")
    payload = json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    with temporary.open("xb") as destination:
        destination.write(payload)
        destination.flush()
        os.fsync(destination.fileno())
    # On Windows rename fails if destination exists. A partial remains on failure/cancellation.
    temporary.rename(path)


def alignable(text: str) -> str:
    return "".join(character for character in text if character == "'" or unicodedata.category(character)[0] in ("L", "N"))


def inspect_wave(path: Path):
    with path.open("rb") as source:
        header = source.read(12)
        if len(header) != 12 or header[:4] != b"RIFF" or header[8:] != b"WAVE" or int.from_bytes(header[4:8], "little") + 8 != path.stat().st_size:
            raise ValueError("A complete bounded RIFF WAV window is required.")
    with wave.open(str(path), "rb") as source:
        frames = source.getnframes()
        if (source.getframerate(), source.getnchannels(), source.getsampwidth(), source.getcomptype()) != (16000, 1, 2, "NONE"):
            raise ValueError("Only prepared 16 kHz mono PCM16 windows are accepted.")
        if frames < 1 or frames > MAXIMUM_AUDIO_FRAMES:
            raise ValueError("Each alignment window must contain positive audio and at most 180 seconds.")
        pcm = source.readframes(frames)
        if len(pcm) != frames * 2 or source.readframes(1):
            raise ValueError("The WAV window has missing or extra PCM frames.")
    return frames, pcm


def run() -> int:
    sys.dont_write_bytecode = True
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    if sys.argv[1:] == ["--version"]:
        print(VERSION)
        print("projectecho-aligner-protocol: " + PROTOCOL)
        print("python=" + sys.version.split()[0] + "; official ForcedAligner only; offline CPU float32; no model loaded")
        return 0
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--task", required=True, choices=("align",))
    parser.add_argument("--family", required=True, choices=("qwen3_forced_aligner",))
    parser.add_argument("--model", required=True)
    parser.add_argument("--backend", required=True, choices=("cpu",))
    parser.add_argument("--threads", type=int, default=6)
    parser.add_argument("--request-sequence", required=True)
    parser.add_argument("--words-out", required=True)
    parser.add_argument("--batch-manifest-out")
    parser.add_argument("--metrics", action="store_true")
    parser.add_argument("--log-file")
    args = parser.parse_args()
    if not 1 <= args.threads <= 64:
        raise ValueError("CPU threads must be 1 through 64.")
    started = time.perf_counter()
    model_directory = local_path(args.model)
    sequence = local_path(args.request_sequence)
    prefix = local_path(args.words_out, exists=False)
    if not model_directory.is_dir() or prefix.suffix.lower() != ".json" or not prefix.parent.is_dir():
        raise ValueError("Select an official local model directory and an existing output directory with a JSON prefix.")
    configuration = read_json(local_path(str(model_directory / "config.json")))
    thinker = configuration.get("thinker_config", {})
    if configuration.get("model_type") != "qwen3_asr" or thinker.get("model_type") != "qwen3_forced_aligner" or thinker.get("classify_num") != 5000 or configuration.get("timestamp_segment_time") != 80:
        raise ValueError("This bridge requires the verified original official Qwen3-ForcedAligner architecture, not an ASR/GGUF/HF-converted model.")
    model_weights = local_path(str(model_directory / "model.safetensors"))
    requests = read_json(sequence)
    if not isinstance(requests, list) or not 1 <= len(requests) <= MAXIMUM_REQUESTS:
        raise ValueError("A batch must contain 1 through 16 requests.")
    ids = set()
    prepared = []
    for request in requests:
        if not isinstance(request, dict) or set(request) != {"id", "audio", "text", "language"}:
            raise ValueError("Each request must have exactly id/audio/text/language.")
        identifier, text, language = request["id"], request["text"], request["language"]
        if not isinstance(identifier, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", identifier) or identifier.casefold() in ids:
            raise ValueError("Request IDs must be unique safe local filename components.")
        ids.add(identifier.casefold())
        if not isinstance(text, str) or not text.strip() or len(text) > MAXIMUM_TEXT_CHARACTERS or "\0" in text:
            raise ValueError("Alignment text must be nonempty, bounded and contain no NUL.")
        text.encode("utf-8", errors="strict")
        if not isinstance(language, str) or language not in ("Chinese", "English") or language not in configuration.get("support_languages", []):
            raise ValueError("This verified M4 bridge supports only canonical Chinese/English requests; Japanese and other language routes are not enabled.")
        audio_path = local_path(request["audio"])
        frames, pcm = inspect_wave(audio_path)
        words_path = prefix.with_name(prefix.stem + "_" + identifier + ".json")
        diagnostic_path = words_path.with_name(words_path.stem + ".repair-diagnostics.json")
        if words_path.exists() or diagnostic_path.exists():
            raise FileExistsError("Use a new attempt; existing word/diagnostic outputs are preserved.")
        prepared.append((request, audio_path, frames, pcm, words_path, diagnostic_path))
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1",
                      DO_NOT_TRACK="1", GRADIO_ANALYTICS_ENABLED="False", TOKENIZERS_PARALLELISM="false",
                      OMP_NUM_THREADS=str(args.threads), MKL_NUM_THREADS=str(args.threads))
    # Local library caches stay within this attempt and are never implicit downloads/global writes.
    cache = prefix.parent / "python-cache"
    for variable in ("HF_HOME", "HF_HUB_CACHE", "NUMBA_CACHE_DIR", "LIBROSA_CACHE_DIR", "TORCHINDUCTOR_CACHE_DIR"):
        target = cache / variable.lower()
        local_path(str(target), exists=False)
        target.mkdir(parents=True, exist_ok=True)
        os.environ[variable] = str(target)
    import_start = time.perf_counter()
    import importlib.metadata
    import numpy as np
    import torch
    from qwen_asr import Qwen3ForcedAligner
    if importlib.metadata.version("qwen-asr") != "0.0.6" or importlib.metadata.version("transformers") != "4.57.6":
        raise ValueError("Use the validated isolated official Python runtime versions.")
    torch.set_num_threads(args.threads)
    torch.set_num_interop_threads(1)
    import_ms = (time.perf_counter() - import_start) * 1000
    load_start = time.perf_counter()
    wrapper = Qwen3ForcedAligner.from_pretrained(str(model_directory), dtype=torch.float32, device_map="cpu",
                                               attn_implementation="sdpa", local_files_only=True, use_safetensors=True)
    model_load_ms = (time.perf_counter() - load_start) * 1000
    processor_source = local_path(sys.modules[type(wrapper.aligner_processor).__module__].__file__)
    if {str(parameter.device) for parameter in wrapper.model.parameters()} != {"cpu"} or {parameter.dtype for parameter in wrapper.model.parameters()} != {torch.float32}:
        raise ValueError("The selected aligner did not load solely on CPU in float32.")
    if wrapper.timestamp_segment_time != 80 or wrapper.model.thinker.lm_head.out_features != 5000:
        raise ValueError("Loaded model differs from the verified acoustic timestamp classifier.")
    print("projectecho-aligner-protocol: " + PROTOCOL)
    print("projectecho-aligner-backend: CPU", file=sys.stderr, flush=True)
    print("projectecho-aligner-device: PyTorch CPU " + torch.backends.cpu.get_cpu_capability() + "; float32; threads=" + str(torch.get_num_threads()), file=sys.stderr, flush=True)
    completed = []
    for request, audio_path, frames, pcm, words_path, diagnostic_path in prepared:
        tick = time.perf_counter()
        audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
        word_list, input_text = wrapper.aligner_processor.encode_timestamp(request["text"], request["language"])
        if not 1 <= len(word_list) <= MAXIMUM_WORDS or "".join(alignable(word) for word in word_list) != alignable(request["text"]):
            raise ValueError("The official tokenization does not exactly cover the bounded original text.")
        inputs = wrapper.processor(text=[input_text], audio=[audio], return_tensors="pt", padding=True)
        inputs = inputs.to(wrapper.model.device).to(wrapper.model.dtype)
        with torch.inference_mode():
            logits = wrapper.model.thinker(**inputs).logits
        output_ids = logits.argmax(dim=-1)
        raw_ms = (output_ids[0][inputs["input_ids"][0] == wrapper.timestamp_token_id] * wrapper.timestamp_segment_time).to("cpu").numpy()
        if raw_ms.size != 2 * len(word_list):
            raise ValueError("Raw acoustic predictions do not cover every official word.")
        words = [{"word": word, "start_sample": int(raw_ms[index * 2] * 16), "end_sample": int(raw_ms[index * 2 + 1] * 16), "confidence": 0}
                 for index, word in enumerate(word_list)]
        # Call the official repair only after raw extraction, solely to retain transparent diagnostics.
        repaired = wrapper.aligner_processor.parse_timestamp(word_list, raw_ms.copy())
        changes = [{"boundary": index, "raw_ms": int(raw_ms[index]),
                    "repaired_ms": repaired[index // 2]["start_time" if index % 2 == 0 else "end_time"]}
                   for index in range(raw_ms.size)
                   if raw_ms[index] != repaired[index // 2]["start_time" if index % 2 == 0 else "end_time"]]
        atomic_json(words_path, words)
        atomic_json(diagnostic_path, {"schema": "projectecho-official-aligner-raw-audit-1", "id": request["id"],
                                     "input_sha256": hash_file(audio_path), "frames": frames,
                                     "text_sha256": hashlib.sha256(request["text"].encode("utf-8")).hexdigest(),
                                     "raw_words_sha256": hash_file(words_path), "repair_changes": changes,
                                     "repair_used_for_output": False, "timestamp_quantum_ms": 80})
        if any(word["start_sample"] < 0 or word["end_sample"] < word["start_sample"] or word["end_sample"] > frames for word in words) or any(
                a["start_sample"] > b["start_sample"] or a["end_sample"] > b["end_sample"] for a, b in zip(words, words[1:])):
            raise ValueError("Raw alignment has reversed/out-of-audio/nonmonotonic predictions; outputs retained without successful completion or repair.")
        elapsed_ms = (time.perf_counter() - tick) * 1000
        completed.append({"id": request["id"], "words": str(words_path), "frames": frames, "inference_ms": elapsed_ms,
                          "raw_words_sha256": hash_file(words_path), "repair_change_count": len(changes)})
        print(RAW_MARKER, file=sys.stderr, flush=True)
        print("projectecho-aligner-complete: " + request["id"], file=sys.stderr, flush=True)
        if args.metrics:
            print("metrics.inference_ms[" + request["id"] + "]=" + format(elapsed_ms, ".3f"), flush=True)
        del inputs, logits, output_ids, audio
    metrics = {"version": VERSION, "protocol": PROTOCOL, "actual_backend": "CPU", "dtype": "float32", "threads": args.threads,
               "python_version": sys.version, "torch_version": torch.__version__, "numpy_version": np.__version__,
               "transformers_version": importlib.metadata.version("transformers"), "qwen_asr_version": importlib.metadata.version("qwen-asr"),
               "processor_source_file": str(processor_source), "processor_source_sha256": hash_file(processor_source),
               "supported_request_languages": ["Chinese", "English"],
               "model": str(model_directory), "model_weights": str(model_weights), "import_ms": import_ms, "model_load_ms": model_load_ms,
               "wall_ms": (time.perf_counter() - started) * 1000, "requests": completed, "asr_model_loaded": False,
               "raw_predictions": True, "timestamp_repair_used": False}
    if args.batch_manifest_out:
        atomic_json(local_path(args.batch_manifest_out, exists=False), metrics)
    if args.log_file:
        path = local_path(args.log_file, exists=False)
        if path.exists():
            raise FileExistsError("Existing stage logs are preserved.")
        with path.open("x", encoding="utf-8") as destination:
            destination.write(json.dumps(metrics, ensure_ascii=False) + "\n")
            destination.flush()
            os.fsync(destination.fileno())
    if args.metrics:
        print("metrics.import_ms=" + format(import_ms, ".3f"))
        print("metrics.model_load_ms=" + format(model_load_ms, ".3f"))
        print("metrics.wall_ms=" + format(metrics["wall_ms"], ".3f"), flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(run())
    except Exception as error:
        print("projectecho-aligner-error: " + type(error).__name__ + ": " + str(error), file=sys.stderr, flush=True)
        raise SystemExit(1)
