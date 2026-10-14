# iPhoneMirror pre6 integrated acceptance plan (2026-10-14)

Status: **IN PROGRESS, NOT RELEASED**. This document is a development/validation checklist, not evidence of implementation.
Baseline branch: `feature/pre6-integrated-20261014`.
Last previously verified successful pre6 experimental CI artifact: run 38522243857 at commit `e083663be16ecb127b7fad7432673d10dc4d3346`. That artifact is NOT the integrated release.

## Delivery rules

- Preserve all pre5.1/pre6 supported features and USB video-capture work.
- Do not claim genuine 120fps from a 120fps UI setting, 120Hz monitor, FFmpeg nominal FPS, repeated frames, or interpolated frames.
- A3/B3/A4/B4 AirPlay keep-alive experiments remain unfinished. Any keepAliveLowPower changes create a separate new experimental variant.
- Every checklist item requires four statuses: source implemented; automated test passed; CI package built; Windows+iPhone hardware acceptance passed. Do not mark untested as complete.
- Do not release an older artifact under a new name. The CI output must identify its Git commit SHA and include a digest.

## Track A — pre6 integrated stability release (release gating)

- [ ] Replace bundled `src/App/Assets/iphone16_pro_max_shell.png` (currently a low-resolution 240x509 indexed PNG at last inspection) with the user's original 994x2048 RGBA PNG; verify exact original SHA256 against the supplied asset and inspect alpha cutout. Do not substitute screenshots or regenerate it. Build-time embedded resource must be immutable to user UI/configuration; no legacy fallback.
- [ ] Remove every user-level replace/import-shell route while retaining the "include shell" toggle and editor translate/scale/rotation controls.
- [ ] Align main-window, independent-window, screenshot, recorder, streamer, and layout editor geometry/corners/alpha mask; no duplicate Dynamic Island; verify full-frame portrait/landscape and rotation.
- [ ] Default background: selectable plain #FFFFFF 9:16 (1080x1920 default); no file dependency. Preserve custom JPG/PNG backgrounds and normalized-position layout editing regardless of shell-checkbox state.
- [ ] Recording editor: translation, scale, 0/90/180/270 degrees, persistence, preview/output pixel alignment. Avoid duplicated compositing implementation.
- [ ] Output: optional 720p/1080p/1440p/4K presets in 9:16 or 16:9 according to canvas, custom dimensions with safe even-dimension normalization, 30/60/75/90/120 requested-FPS presets, and recommended/custom bitrate. Distinguish target FPS from measured unique source FPS.
- [ ] Background compositing and shell overlay independent of low-resolution backdrop pixel size. Prefer single final encode, shared geometry and GPU surfaces; no unnecessary intermediate MP4 transcodes. Identify actual pixel quality and timing costs.
- [ ] AirPlay mirror->media-playback->mirror transition: log real session/source changes; route new media frames to both previews; bounded frame retention/recovery; correctly report unsupported protected/DRM sources. Do not fake recoveries by indefinitely recycling stale frames.
- [ ] Independent-window rotation: when source/session orientation unambiguously changes, animate -90 degrees (~220-320 ms) and return on exit; debounce orientation and keep encoded timestamps continuous.
- [ ] Stabilize **actual 60fps first** on the current 60fps AirPlay source. Fix avoidable capture->decode->preview->record frame loss and WPF/DirectComposition scheduling contention; record input callbacks/PTS, decoder processed frames, IPC queue drops, render submitted/presented, and writer frame count separately. Present counters are not equivalent to fresh source frames.
- [ ] WASAPI audio underruns: inspect queue producer/consumer, input 44.1kHz conversion, clock drift, startup fill and backpressure. Aggregate/rate-limit repeating logs so logging itself does not exacerbate jitter, without suppressing aggregate counts or diagnostics. Verify audio continuity and A/V sync.
- [ ] Show the detected active Windows refresh rate (user reports 200Hz), chosen local render cap (<=120Hz for pre6 goal), negotiated wireless source FPS limit (currently <=60), observed unique video FPS and encoder FPS as *different* metrics. Do not label `monitor_cap=120` as a 200Hz panel's actual refresh without verifying the underlying API.
- [ ] Preserve OBS, Ctrl+wheel zoom, audio, independent-window, multi-device sessions, and wired path. Run 30min+ iPhone 16 Pro Max acceptance, including recording with both backgrounds and with/without shell.
- [ ] Build Release Windows x64 portable ZIP from the integrated branch, archive SHA256 digest and per-check test evidence. One unzip should expose an immediately executable app folder.

## Track B — experimental AirPlay >60fps source research (parallel and *not* release gating)

Gate 0 (measurement): on an unmodified 60fps profile, measure callback-frame timestamps/sequence, D3D submitted/presented frames, OS display refresh, recording encoded frames, and duplicated frames. Record conditions and raw log excerpts.

- [ ] Experiment R60 (control): existing 5120x2880@60 or 1080p@60 AirPlay advertising and the original receiver backend; 200Hz Windows monitor. Preserve receiver/session settings and log full resolution/FPS negotiation.
- [ ] Experiment R90/R120: **separate receiver capability advertising variants** with 90/120fps, not just local `SetDeviceVideoPreferences`. Instrument AirPlay SETUP and the actual video sender's timestamps. Isolate backend code and provide a revert switch. Do not assume the iPhone supports the offered FPS.
- [ ] Cross-check with a second capable AirPlay receiver backend at corresponding profiles where it supports requesting a higher frame rate. A requested cap alone is not evidence of >60fps.
- [ ] Local renderer synthetic-frame benchmark at 60/90/120, separately from AirPlay, to establish that DXGI/WPF/compositor path can sustain 120 fresh frames/s on the 200Hz display without requiring new iPhone frames.
- [ ] Only declare *genuine* 90/120 input if sustained observed source-frame PTS/callback cadence and non-duplicated frame identity (using an appropriately changing test scene) support it. Track at least 30s stable measurements and repeated sessions, plus A/V sync.
- [ ] If sender still limits to ~60fps after controlled receiver profile experiments, report source/protocol limitation and stop advertising a working genuine 120fps AirPlay mode; consider independent wired capture alternatives only if genuine throughput is verified.

## Baseline evidence and blockers

- From user-provided capture.log on Oct 14: AirPlay host advertised `5120x2880@60`, session capture reported ~59.56fps early in the run, renderer logged `requested_cap=60`, `monitor_cap=120` at that point and substantial WASAPI underruns. Higher local cap later is not proof of >60fps sender input.
- The monitor is reported as 200Hz by the user; check Windows active output mode before diagnosing an OS cap.
- Original provided shell source and its SHA256 must be staged into the repository. Old 240x509 placeholder is *not* accepted.
- Track B failure must not indefinitely block stable Track A functionality. Keep AirPlay keep-alive A/B groups isolated as agreed.

## Release evidence template

| Area | Commit / files | Code | Automated tests | Windows CI | Hardware test | Known limitation |
|---|---|---|---|---|---|---|
| Shell asset | | | | | | |
| Background + editor | | | | | | |
| Media switch + rotation | | | | | | |
| 60fps rendering/audio | | | | | | |
| Recording/export | | | | | | |
| 120fps AirPlay experiments | | | | | | |

For each result attach reproduction instructions, measurement setup, and original timestamps; never silently convert unverified hypotheses to completed findings.
