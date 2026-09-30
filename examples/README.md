# Synthetic only
These JSON examples contain no real meeting content. Referenced audio files intentionally do not exist; planned/nullable hash fields are not completed recordings. Schema validation does not prove platform or audio-engine correctness. All production Complete exports must have actual hashes and byte sizes. Settings are design defaults, not executable application configuration yet.

`meeting.synthetic.json` now uses meeting schema v2. Its `captureIndexPath` is a production-layout path; the matching fixture is `capture-index.synthetic.jsonl`. Validate every line separately against `schemas/capture-index-record.schema.json`. All 15 rows are synthetic with null hashes and hypothetical PCM/header lengths; no file was captured, flushed or recovered.

The example uses four intentionally large synthetic packets to keep the 40-second timeline readable; it does not assert that WASAPI returns packets of that size or permit production code to discard individual packet timestamps. Paused interval 15–20 seconds remains on the complete timeline; mic is enabled only for 5–15 and 20–35 seconds. Format belongs to each segment, not the whole source. Production must verify record hashes, sequence, frame coverage, control boundaries and actual files beyond schema validation.

`tests/algorithm-vectors.json` records expected behavior only. The compact timeline vector is explicitly deferred beyond M2. M2 renders the complete elapsed timeline. No synthetic vector or schema check counts as a Windows capture, privacy, drift or crash-recovery test.
