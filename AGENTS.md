# ProjectEcho — Codex repository instructions

## Mission / scope
Build a Windows portable meeting recorder for Zoom and Webex desktop + browser computer-audio meetings. Capture remote/app audio and an independently consented microphone track. Preserve recoverable originals; always offer a true mixed listening/upload file. Local ASR and analysis are optional; manual external upload is supported without building an API client.

## Start each session
Read `docs/00_HANDOFF.md`, `STATUS.md`, `docs/12_MILESTONES.md` and this file. Read only the stage-specific docs needed next. Use Chinese explanations. Keep the chat short; place long reports in Markdown. Never mark planned, mocked, Linux-only or unexecuted Windows checks as passed.

## Hard constraints
- Work only inside the authorized project folder. Do not modify the user's other projects, global proxy, registry policy, global PATH, Git identity or browser profile.
- No admin rights, virtual audio driver, WSL, Docker, Node or Python dependency for normal recording/export use. Developer needs .NET SDK; end-user self-contained publish must not need SDK/.NET preinstallation. Inspect existing tools before adding any. No downloaded binaries in this handoff are implied.
- Do not start recording, open private meetings or enable microphone collection without explicit user action. Synthetic tests are preferred. Mic disabled means no mic PCM is collected/stored, not just zero volume in a mix.
- Do not upload audio, transcripts, meeting URLs, profiles, logs, model prompts or source data. No API keys or external integrations in the MVP. A request to export is not authorization to upload.
- Never capture all system audio silently when app capture fails. Fallback requires explicit scope confirmation. Do not change a missing microphone automatically.
- Application loopback is a PROCESS TREE boundary, not a tab, not a participant. Ordinary separate windows/incognito/profile-directory are not proof of isolation. Dedicated browser uses a separate user-data-dir and must be tested.
- Do not claim platform mute equals recorder mute; do not scrape UI for a mandatory mute-sync feature. App audio source is not automatically a person; local mic may contain multiple people/echo.
- Record core, export, ASR and analysis are fault-isolated. ASR is never on the recording callback path. Postprocessing must not destroy originals or block the next meeting.
- Audio blocks need actual timestamps/frame counts. No arrival-time-only alignment; no concatenating two different sources as though mixing; no `-shortest` loss of a remaining source. Do not promise that `amix` or resampling alone solves clock drift.
- Implement the current locked NAudio API, not examples copied from 2.x. Verify in a buildable spike. Do not invent class/method names or NuGet versions. Candidate baseline is .NET 10 LTS and NAudio 3.1.0 as verified on 2026-09-28; SDK patch is resolved on the real machine.
- Respect organization restrictions. No browser sandbox weakening, remote debugging, certificate bypass, code injection, policy bypass, or automatic extension installation.
- No force push, `git clean -fdx`, recursive cleanup outside generated paths, or secret/meeting data in Git. Any remote push needs user authorization.

## Delivery order / definition of done
Implement only the requested milestone; M0 first. M0 must expose actual source selection and preflight, compile on Windows and report what was tested. Later stage work cannot substitute for failed capture.
M2 is the first useful release: Zoom + Webex desktop and Chrome dedicated-browser paths, remote+optional mic, crash-aware storage, real mixed file, manual size-limited export, no model required.
M2 must include a complete win-x64 self-contained folder and clean-Windows validation without preinstalled SDK/.NET, with dependency provenance/licenses. M3/M4/M5 are not release prerequisites. Require WAV plus at least one verified compressed format (MP3 or M4A); the second compressed format is optional. Keep the full elapsed timeline; defer trimming, removing pauses, speech-boundary cutting, diagnostic stereo UI, loudness editing and terminology management.
Default to an unclassified project and a timestamp title, allow filing after recording, and use one bookmark type. Short test recording is user-triggered; source scope and mic state remain visible each meeting. Disable application playback until recording has finished saving, including Paused. Preserve one CaptureHost. User decision 2026-10-01 supersedes the earlier SQLite deferral: use SQLite for rebuildable meeting and transcript indexes in M3C while preserving authoritative originals and clock metadata. Defer generic job/plugin frameworks until needed.
Stop must retain valid pre-cutoff queued frames and finish writing before Saved. Pause/MicOff must acknowledge the actual capture-thread boundary and stop collection, with no post-boundary mic PCM persisted. Persist enough segment/packet clock metadata to rebuild the timeline from disk alone. Synthetic boundary, health, disk-budget and real long-run tests remain mandatory.
After each stage: build → automated tests → appropriate manual Windows test → diff/review → update `STATUS.md`, `tests/acceptance.csv`, and local test evidence. Report implemented / tested / not tested / blocked separately. Record exact OS/build, versions, selected source mode and evidence path; redact identifying data.

## Intended structure / commands
Create `ProjectEcho.slnx`, `src/ProjectEcho.App`, `src/ProjectEcho.Core`, `src/ProjectEcho.Capture.Windows`, `src/ProjectEcho.CaptureHost`, `src/ProjectEcho.Export`, `tests/ProjectEcho.Tests` during M0/M1. Additional workers only when needed.
Use the installed or project-local .NET SDK. `scripts/Dev.ps1 -Action Build|Test|Run|Publish` is a wrapper for the intended structure, not proof that the app already exists. If it reports missing projects, create them in M0; do not falsify success.
Use `scripts/Dotnet.ps1` for direct CLI calls so SDK caches and temporary files stay project-local. SDK selection is pinned in global.json; dependency downloads are permitted for the user's explicitly requested environment setup, not for automatic recording-time downloads. Do not treat environment smoke projects in .local as M0 implementation or product acceptance.

## Review priorities
Missing end-of-meeting audio; microphone privacy; browser audio scope; PID reuse/reparenting; playback feedback; silent data corruption; no-data vs silence; pause gaps/time mapping; compressed file completeness; leakage in export/Git; fabricated transcript or tasks. Use the traceability IDs.
