"""Standalone official ForcedAligner evaluation; never imports/loads an ASR checkpoint.

Use the PowerShell companion to hold the shared inference benchmark lock. All inputs are
hash-pinned local public/synthetic fixtures and all HF/model access is forced offline.
The original official API result and its raw acoustic predictions are saved separately.
"""
from __future__ import annotations

import argparse
import dataclasses
import hashlib
import importlib.metadata
import inspect
import json
import os
from pathlib import Path
import platform
import sys
import threading
import time
import traceback
import wave


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def contained(path: Path, root: Path) -> bool:
    return path.resolve().is_relative_to(root.resolve())


class MemorySampler:
    def __init__(self, psutil):
        self.process = psutil.Process()
        self.stage = "imports"
        self.samples = []
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._sample, daemon=True)
        self.thread.start()

    def _sample(self):
        while not self.stop.is_set():
            value = self.process.memory_info()
            self.samples.append({"monotonic": time.perf_counter(), "stage": self.stage,
                                 "rss": value.rss, "private": getattr(value, "private", value.vms),
                                 "os_peak_working_set": getattr(value, "peak_wset", None)})
            self.stop.wait(0.1)

    def close(self):
        self.stop.set()
        self.thread.join(timeout=2)


def native_comparison(fixture, words):
    reference = fixture.get("GenericBaselineWords")
    if not reference:
        return {"status": "NO_SAME_INPUT_NATIVE_WORD_REFERENCE"}
    previous = json.loads(Path(reference).read_text(encoding="utf-8-sig"))
    same_words = [row["word"] for row in previous] == [row["word"] for row in words]
    if not same_words:
        return {"status": "TOKENIZATION_DIFFERS", "reference": reference,
                "reference_sha256": sha256(Path(reference)),
                "native_words": [row["word"] for row in previous]}
    differences = [abs(a[key] - b[key]) for a, b in zip(previous, words)
                   for key in ("start_sample", "end_sample")]
    return {"status": "SAME_WORD_SEQUENCE", "reference": reference,
            "reference_sha256": sha256(Path(reference)),
            "exact_boundary_count": sum(value == 0 for value in differences),
            "boundary_count": len(differences), "maximum_difference_frames": max(differences, default=0),
            "maximum_difference_ms": max(differences, default=0) / 16,
            "mean_difference_ms": sum(differences) / max(1, len(differences)) / 16}


def raw_contract(words, frames):
    return {"word_count": len(words),
            "negative_count": sum(row["start_sample"] < 0 or row["end_sample"] < 0 for row in words),
            "out_of_audio_count": sum(row["start_sample"] > frames or row["end_sample"] > frames for row in words),
            "reversed_word_count": sum(row["end_sample"] < row["start_sample"] for row in words),
            "zero_word_count": sum(row["start_sample"] == row["end_sample"] for row in words),
            "nonmonotonic_start_or_end_count": sum(a["start_sample"] > b["start_sample"] or a["end_sample"] > b["end_sample"]
                                                    for a, b in zip(words, words[1:])),
            "note": "Internal zero-span words are not repaired. These checks are not sentence or semantic accuracy acceptance."}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--fixtures", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--engine", choices=("legacy", "hf"), default="legacy")
    parser.add_argument("--dtype", choices=("float32", "bfloat16"), default="float32")
    parser.add_argument("--threads", type=int, default=6)
    parser.add_argument("--warm-repeats", type=int, default=2)
    parser.add_argument("--ids", default="long02-window-000000,long02-window-000001,long02-window-000002,independent-boundaries")
    parser.add_argument("--compile", action="store_true")
    parser.add_argument("--request-sequence", default=None)
    args = parser.parse_args()
    project = Path(__file__).resolve().parent.parent
    output, fixture_path, model_path = Path(args.output).resolve(), Path(args.fixtures).resolve(), Path(args.model).resolve()
    if not contained(output, project / ".local" / "M4Perf") or not contained(fixture_path, project):
        raise ValueError("Evaluation outputs and fixtures must remain in the authorized project.")
    if not (contained(model_path, project / ".tools") or contained(model_path, Path("H:/AI/Qwen"))):
        raise ValueError("Only the authorized separately deployed local model is accepted.")
    if output.exists() and any(output.iterdir()):
        raise FileExistsError("Use a new evidence directory; previous runs are not overwritten.")
    output.mkdir(parents=True, exist_ok=True)
    if not 1 <= args.threads <= 16 or not 0 <= args.warm_repeats <= 4:
        raise ValueError("Bounded CPU threads and warm repeats are required.")
    cache = project / ".local" / "M4Perf" / "python" / "cache"
    for key in ("HF_HOME", "HF_HUB_CACHE", "NUMBA_CACHE_DIR", "LIBROSA_CACHE_DIR", "TORCHINDUCTOR_CACHE_DIR"):
        target = cache / key.lower()
        target.mkdir(parents=True, exist_ok=True)
        os.environ[key] = str(target)
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1",
                      DO_NOT_TRACK="1", GRADIO_ANALYTICS_ENABLED="False", TOKENIZERS_PARALLELISM="false",
                      OMP_NUM_THREADS=str(args.threads), MKL_NUM_THREADS=str(args.threads))
    sys.stdout.reconfigure(encoding="utf-8")
    started = time.perf_counter()
    selected_ids = args.ids.split(",")
    manifest = json.loads(fixture_path.read_text(encoding="utf-8-sig"))
    by_id = {row["Id"]: row for row in manifest["Fixtures"]}
    fixtures = [by_id[key] for key in selected_ids]
    shared_requests = None
    if args.request_sequence:
        shared_path = Path(args.request_sequence).resolve()
        if not contained(shared_path, project):
            raise ValueError("Shared request sequence must be project-local.")
        shared_requests = json.loads(shared_path.read_text(encoding="utf-8-sig"))
        expected_requests = [{"id": row["Id"], "audio": row["Audio"], "text": row["Text"], "language": row["Language"]} for row in fixtures]
        if shared_requests != expected_requests:
            raise ValueError("Shared request sequence differs from the selected exact fixture order/text/audio.")
    for fixture in fixtures:
        source = Path(fixture["Audio"])
        if not contained(source, project) or sha256(source) != fixture["InputSha256"]:
            raise ValueError("The exact public/synthetic audio fixture changed.")
    model_file = model_path / "model.safetensors"
    provenance = json.loads((model_path / "projectecho-download-provenance.json").read_text(encoding="utf-8-sig"))
    expected_model_hash = next(row["Sha256"] for row in provenance["Files"] if row["File"] == "model.safetensors")
    if sha256(model_file) != expected_model_hash:
        raise ValueError("Official model differs from its locked download provenance.")
    report = {"schema": "m4-official-python-aligner-evaluation-1", "engine": args.engine, "dtype": args.dtype,
              "threads": args.threads, "warm_repeats": args.warm_repeats, "compile_requested": args.compile,
              "python": sys.version, "executable": sys.executable, "prefix": sys.prefix,
              "platform": platform.platform(), "model_directory": str(model_path), "model_provenance": provenance,
              "fixture_manifest": str(fixture_path), "fixture_manifest_sha256": sha256(fixture_path),
              "validation_ms": (time.perf_counter() - started) * 1000, "runs": [], "errors": [],
              "inference_device": "cpu", "asr_model_loaded": False, "network_inference": False,
              "audio_captured": False, "microphone_collected": False,
              "measurement_note": "Cold means a fresh model in this process, not a purged disk cache. Warm calls retain only this aligner. Exact old 30-second inputs are compared with old 30-second native runs, not short-clip extrapolation."}
    if shared_requests is not None:
        report["shared_request_sequence_sha256"] = sha256(Path(args.request_sequence))
    sampler = None
    hook = None
    try:
        import_start = time.perf_counter()
        import numpy as np
        import psutil
        import torch
        import transformers
        sampler = MemorySampler(psutil)
        torch.set_num_threads(args.threads)
        torch.set_num_interop_threads(1)
        dtype = getattr(torch, args.dtype)
        report.update(torch=torch.__version__, transformers=transformers.__version__,
                      cuda_available=torch.cuda.is_available(), torch_cuda=torch.version.cuda, torch_hip=torch.version.hip,
                      cpu_capability=torch.backends.cpu.get_cpu_capability(), mkldnn_enabled=torch.backends.mkldnn.enabled,
                      interop_threads=torch.get_num_interop_threads(), import_ms=(time.perf_counter() - import_start) * 1000)
        report["torch_configuration"] = torch.__config__.show()
        sampler.stage = "model-load"
        load_start = time.perf_counter()
        if args.engine == "legacy":
            from qwen_asr import Qwen3ForcedAligner
            wrapper = Qwen3ForcedAligner.from_pretrained(str(model_path), dtype=dtype, device_map="cpu",
                                                       attn_implementation="sdpa", local_files_only=True)
            model, processor = wrapper.model, wrapper.processor
            report["official_source_file"] = inspect.getfile(Qwen3ForcedAligner)
            report["official_source_sha256"] = sha256(Path(report["official_source_file"]))
            captured = {}

            def capture_forward(_module, _args, kwargs, returned):
                # Public PyTorch observer only; returns None and never changes tensors or predictions.
                captured["input_ids"] = kwargs["input_ids"].detach()
                captured["logits"] = returned.logits.detach()

            hook = model.thinker.register_forward_hook(capture_forward, with_kwargs=True)
            if args.compile:
                model.thinker.forward = torch.compile(model.thinker.forward)
        else:
            from transformers import AutoModelForTokenClassification, AutoProcessor
            processor = AutoProcessor.from_pretrained(str(model_path), local_files_only=True)
            model = AutoModelForTokenClassification.from_pretrained(str(model_path), dtype=dtype, device_map="cpu",
                                                                    attn_implementation="sdpa", local_files_only=True)
            report["official_source_file"] = inspect.getfile(type(processor))
            report["official_source_sha256"] = sha256(Path(report["official_source_file"]))
            if args.compile:
                model.forward = torch.compile(model.forward)
        report["model_load_ms"] = (time.perf_counter() - load_start) * 1000
        report["model_class"] = type(model).__name__
        report["parameter_count"] = sum(parameter.numel() for parameter in model.parameters())
        report["actual_parameter_devices"] = sorted({str(parameter.device) for parameter in model.parameters()})
        report["actual_parameter_dtypes"] = sorted({str(parameter.dtype) for parameter in model.parameters()})
        if report["actual_parameter_devices"] != ["cpu"]:
            raise RuntimeError("The measured official model was not actually on CPU.")
        write_json(output / "run-in-progress.json", report)
        for fixture in fixtures:
            with wave.open(fixture["Audio"], "rb") as wav:
                if (wav.getframerate(), wav.getnchannels(), wav.getsampwidth(), wav.getnframes()) != (16000, 1, 2, fixture["FrameCount"]):
                    raise ValueError("Fixture audio format/frame count differs.")
                audio = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
            expected_text_hash = hashlib.sha256(fixture["Text"].encode("utf-8")).hexdigest()
            for repeat in range(args.warm_repeats + 1):
                identifier = f'{fixture["Id"]}-{repeat}'
                sampler.stage = "inference:" + identifier
                tick, cpu_tick = time.perf_counter(), time.process_time()
                print(f"START {args.engine} {args.dtype} {identifier}", flush=True)
                if args.engine == "legacy":
                    captured.clear()
                    result = wrapper.align(audio=(audio, 16000), text=fixture["Text"], language=fixture["Language"])
                    official = [dataclasses.asdict(item) for item in result[0]]
                    elapsed, cpu = (time.perf_counter() - tick) * 1000, (time.process_time() - cpu_tick) * 1000
                    sampler.stage = "raw-output-audit:" + identifier
                    word_list, _ = wrapper.aligner_processor.encode_timestamp(fixture["Text"], fixture["Language"])
                    predictions = captured["logits"].argmax(dim=-1)[0]
                    raw_ms = (predictions[captured["input_ids"][0] == wrapper.timestamp_token_id] * wrapper.timestamp_segment_time).to("cpu").numpy().tolist()
                    captured.clear()
                else:
                    inputs, word_lists = processor.prepare_forced_aligner_inputs(audio=audio, transcript=fixture["Text"],
                                                                                 language=fixture["Language"], return_tensors="pt")
                    inputs = inputs.to(model.device, model.dtype)
                    with torch.inference_mode():
                        outputs = model(**inputs)
                    official = processor.decode_forced_alignment(logits=outputs.logits, input_ids=inputs["input_ids"],
                                                                 word_lists=word_lists, timestamp_token_id=model.config.timestamp_token_id)[0]
                    elapsed, cpu = (time.perf_counter() - tick) * 1000, (time.process_time() - cpu_tick) * 1000
                    sampler.stage = "raw-output-audit:" + identifier
                    word_list = word_lists[0]
                    predictions = outputs.logits.argmax(dim=-1)[0]
                    raw_ms = (predictions[inputs["input_ids"][0] == model.config.timestamp_token_id].float() * processor.timestamp_segment_time).cpu().numpy().tolist()
                    del outputs, inputs
                if len(raw_ms) != 2 * len(word_list) or len(official) != len(word_list):
                    raise ValueError("Raw acoustic classes did not cover the complete official word list.")
                words = [{"word": word, "start_sample": int(round(raw_ms[2 * index] * 16)),
                          "end_sample": int(round(raw_ms[2 * index + 1] * 16)), "confidence": 0}
                         for index, word in enumerate(word_list)]
                changes = [{"boundary": index, "raw_ms": raw_ms[index],
                            "repaired_ms": official[index // 2]["start_time" if index % 2 == 0 else "end_time"] * 1000}
                           for index in range(len(raw_ms))
                           if abs(raw_ms[index] - official[index // 2]["start_time" if index % 2 == 0 else "end_time"] * 1000) > 0.01]
                write_json(output / (identifier + ".raw-words.json"), words)
                write_json(output / (identifier + ".official-repaired.json"), official)
                row = {"id": fixture["Id"], "repeat": repeat, "first_for_this_input": repeat == 0,
                       "first_model_call": len(report["runs"]) == 0, "inference_ms": elapsed, "cpu_ms": cpu,
                       "audio_seconds": fixture.get("AudioSeconds", fixture["FrameCount"] / fixture["SampleRate"]), "frames": fixture["FrameCount"],
                       "input_sha256": fixture["InputSha256"], "text_sha256": expected_text_hash,
                       "text": fixture["Text"], "raw_output": identifier + ".raw-words.json",
                       "official_output": identifier + ".official-repaired.json", "repair_changes": changes,
                       "raw_contract": raw_contract(words, fixture["FrameCount"]),
                       "native_comparison": native_comparison(fixture, words)}
                report["runs"].append(row)
                write_json(output / "run-in-progress.json", report)
                print(f"DONE {identifier} {elapsed:.3f} ms; repaired boundaries={len(changes)}", flush=True)
        sampler.stage = "final-integrity"
        if sha256(model_file) != expected_model_hash or any(sha256(Path(row["Audio"])) != row["InputSha256"] for row in fixtures):
            raise ValueError("Model or original audio changed during evaluation.")
        report["original_model_and_audio_unchanged"] = True
    except Exception:
        report["errors"].append(traceback.format_exc())
        traceback.print_exc()
    finally:
        if hook is not None:
            hook.remove()
        if sampler is not None:
            sampler.close()
            write_json(output / "memory-samples.json", sampler.samples)
            report["peak_sampled_rss_bytes"] = max((row["rss"] for row in sampler.samples), default=None)
            report["peak_sampled_private_bytes"] = max((row["private"] for row in sampler.samples), default=None)
            report["os_lifetime_peak_working_set_bytes"] = max((row["os_peak_working_set"] or 0 for row in sampler.samples), default=None)
        report["installed_packages"] = sorted([{"name": distribution.metadata["Name"], "version": distribution.version}
                                                for distribution in importlib.metadata.distributions()], key=lambda row: row["name"].lower())
        report["status"] = "COMPLETED_MEASUREMENT" if not report["errors"] else "FAILED"
        report["total_inference_ms"] = sum(row["inference_ms"] for row in report["runs"])
        report["import_load_inference_ms"] = report.get("import_ms", 0) + report.get("model_load_ms", 0) + report["total_inference_ms"]
        report["wall_ms_including_import_load_audit"] = (time.perf_counter() - started) * 1000
        report["timestamp_policy"] = "Official API repairs are preserved separately. Only *.raw-words.json contain unmodified acoustic predictions; no repair values are claimed equivalent to the product raw policy."
        write_json(output / "report.json", report)
    return 0 if not report["errors"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
