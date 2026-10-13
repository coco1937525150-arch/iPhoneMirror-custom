# iPhoneMirror Stage 1 + selected upstream v1.8.5-pre2 integration

## Build base and scope

- Parent: first-stage feature PR #9, plus selected upstream backports PR #10.
- The upstream source reference is RayrenSX/iPhoneMirror `v1.8.5-pre2` (released 2026-10-09).
- This package is **NOT** a wholesale merge of upstream v1.8.5-pre2 and does
  **NOT** claim all upstream features; the included changes are listed below.
- Experimental Windows x64 portable build; separate from the stable installation.

## Included first-stage features (PR #9)

1. Independent window: 0/90/180/270 video rotation synchronized with its
   phone frame, native topmost menu with persisted preference.
2. Dark graphite iPhone 16 Pro Max-inspired frame with visible physical buttons.
   Phone display dimensions and margins are based on the previous fitted frame.
3. Optional phone-shell composition for recording/streaming: blank means a white
   background, or choose a PNG/JPEG/BMP image for the backdrop. Choose a
   custom phone-shell image to reproduce the supplied shell's exact artwork.
   Without the toggle the original raw video encoding path is preserved.
4. Fixed custom shell overlay video aperture and landscape shell/screen geometry.

## Selective upstream v1.8.5-pre2 backports

- DDI: recognize both `Image.trustcache` and `Image.dmg.trustcache` in
  the C# bundle preflight (the bridge already accepted either).
- Recording: cap repeated frame catch-up per timer tick without advancing the
  frame counter when no corresponding rawvideo bytes were written. This avoids
  artificially shortening video relative to real-time audio after stalls.

## Existing features not counted as new

The base branch already contained the upstream AirPlay metadata, routing,
protocol compatibility, audio negotiation and orientation work.
The v1.8.3 bridge `libusb0.dll` PATH / installer workaround also existed.
Do not reapply those patches.

## Upstream pre2 work NOT yet integrated

- AirPlayServer 1.2.5 recovery, stream reuse after TCP EOF and new reconnect
  IPC semantics. These overlap the pending Codex investigation of lock-screen
  TCP EOF / RTSP TEARDOWN and must be reviewed as a separate patchset.
- The broader upstream DDI download/signing/cache overhaul, HID input
  session lifecycle changes and multiple-device control refactor.
- New keyboard mapping, bidirectional clipboard, local automation API,
  first-run wizard, optional component-download manager, system tray and
  microphone-mixing feature. A full pre2 feature parity claim is incorrect.

## Acceptance

- CI compile/test must pass, and the experiment artifact must include
  `iPhoneMirror.exe`, core and AirPlay DLLs, WirelessHost and input bridge.
- On a real iPhone check 90/180/270 rotation, native pinning, and record a
  10-second video with no shell, white background + shell, and chosen image.
- Check pixel cutout, landscape proportions and physical buttons.
- Lock-screen staying connected and reverse control are NOT yet certified.
