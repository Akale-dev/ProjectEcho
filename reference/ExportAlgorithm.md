# Export algorithm sketch (implement/test in C#)

1. Validate source paths and manifest hashes. Create a new exportId; never reuse an unrelated temp directory.
2. Rebuild the common PCM timeline from validated on-disk capture-index records and actual clock mappings. M2 preserves the entire elapsed timeline, filling pauses/gaps with silence and recording the cause. Compact/range editing is deferred; any later edited export needs a piecewise span map.
3. Float-mix remote+enabled mic onto a single stream with explicit gain/headroom. Check single-source and silent cases. Do not place sources back-to-back or rely on a container choosing a track.
4. Derive next interval from min(duration limit, conservative size estimate). M2 uses deterministic time cuts; speech-aware cut selection is deferred. Guarantee end > start and next start advances.
5. Encode each part from PCM independently. Decode/probe it. Check actual byteLength <= maxFileBytes, playable length and mapped start/end. If too large, reduce duration with bounded retry; reject an impossible budget.
6. Write complete part metadata only after validation and final rename. Advance nextStart=end-overlap unless end reaches totalDuration. Keep final short segment; no -shortest truncation.
7. Validate union of part coverage contains all requested timeline ranges. Optional overlap is explicitly listed, never treated as new speech. Save source hashes, codec settings, byte lengths and spans.
8. Expose local output directory to the user. Offer playback only after recording/stopping has ended; prevent this application's own playback during recording or a paused session. Do not open/upload to external providers or attach hidden originals.

Reference vectors: tests/algorithm-vectors.json. These are expected values, not proof of implementation.
