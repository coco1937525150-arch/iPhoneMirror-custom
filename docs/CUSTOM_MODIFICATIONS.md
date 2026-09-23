# Custom Windows mirroring modifications

This source tree contains a focused custom patch on top of the uploaded
`iPhoneMirror` `main` snapshot. It intentionally does **not** implement any
physical-iPhone screen-curtain/lock-screen behavior.

## Implemented scope

### Wired USB resilience

The libusb-1/UsbDk path now tolerates a short Apple composite-device transition
where Windows PnP still has the selected iPhone but the backend temporarily
cannot read `iSerialNumber`.

The fallback remains fail-closed:

- exact serial matching remains first priority;
- physical-topology matching remains the normal temporary-serial fallback;
- PnP fallback is considered only when Windows reports the exact requested
  serial as a present, started Apple parent;
- PnP fallback is permitted only when exactly one physical Apple capture parent
  is attached and exactly one eligible anonymous backend candidate exists;
- a readable different serial is never overridden;
- multi-iPhone/ambiguous cases are rejected rather than cross-bound;
- activation/open/restore perform short bounded settle retries instead of
  treating one transient enumeration miss as a permanent disconnect.

New diagnostics use `usb_identity ... match=pnp_singleton` or `match=none` so a
real-device failure can be separated from decoder/render failures.

### Detached iPhone/iPad hardware shell

Detached native preview windows now resolve an additional hardware-frame profile
from Apple `ProductType` and render it directly in D3D11/DirectComposition.
Supported visual families include:

- Home-button iPhones;
- notched iPhones (including iPhone 16e handling);
- Dynamic Island iPhones;
- iPads.

The shell is procedural (no copyrighted Apple artwork/assets are embedded).
The graphite body, screen bezel, notch/Dynamic Island and transparent outer
corners scale with the window.

The existing detached-window resize/full-screen behavior is preserved. Full
screen intentionally drops the decorative device shell and uses the whole
monitor for the mirrored display.

### Reverse-control coordinate mapping

BLE/wired/wireless reverse control continues to target only the real mirrored
screen rectangle, not the new bezel. New clicks/wheel input on the decorative
bezel are ignored. An already-captured drag is clamped to the screen edge so a
button-up cannot be lost when the pointer crosses into the shell.

### Low-latency path

No CPU image copy was added to the normal preview path. The shell and cutout are
small GPU draw passes around the existing D3D11 video path; AirPlay, USB decode,
audio and Bluetooth control remain otherwise unchanged.

## Intentionally deferred

The following requested ideas are **not** included in this patch:

- turning the physical iPhone display black while mirroring continues;
- a Windows button that toggles such a black-screen mode;
- keeping an actually locked iPhone interactively unlocked only on Windows.

## Build and verification

Use the upstream Windows toolchain documented in `CONTRIBUTING.md`:

```powershell
./build.ps1 -Configuration Release
```

For a quicker compile/test iteration without publishing:

```powershell
./build.ps1 -Configuration Debug -NoPublish
```

Because the USB change is hardware/PnP specific, final verification must be done
on the affected Windows PC with the same iPhone, cable and USB port. After a
wired start, inspect `capture.log` for the backend selection and `usb_identity`
records. Do not publish full UDIDs or pairing data.
