# Third-party notices

Window segmentation, powerset conversion, sample selection and final voting are adapted from `sherpa-onnx/csrc/offline-speaker-diarization-pyannote-impl.h`, sherpa-onnx 1.13.8, commit 11afbd00. Copyright (c) 2024 Xiaomi Corporation. Licensed under Apache-2.0. The complete source attribution and original license must accompany the derived worker.

The `include/onnxruntime_*` headers are unmodified official Microsoft ONNX Runtime v1.22.2 headers, MIT licensed, from https://github.com/microsoft/onnxruntime/tree/v1.22.2/include/onnxruntime/core/session. Runtime execution uses the pinned 1.28.2 DLL, not a newly bundled 1.22.2 runtime.

The worker statically links GCC/libstdc++ runtime under GPL-3.0 with GCC Runtime Library Exception 3.1 and MinGW-w64 runtime under its supplied notices. See generated LICENSES/Sherpa. This does not remove separate notices for sherpa, ONNX Runtime and their compiled dependencies. Segmentation/embedding model licenses remain separately required; models are not included in this native-only overlay.
