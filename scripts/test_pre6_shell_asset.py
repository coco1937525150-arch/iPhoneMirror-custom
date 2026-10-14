#!/usr/bin/env python3
"""Fail CI before compiling if pre6 packages the wrong iPhone 16 Pro Max shell.

This validates the exact original RGBA asset and the shared WPF/recording
viewport profile. It does NOT replace visual or physical-iPhone acceptance.
"""
from __future__ import annotations

import hashlib
from pathlib import Path
import re
import struct
import sys


ROOT = Path(__file__).resolve().parents[1]
ASSET = ROOT / "src/App/Assets/iphone16_pro_max_shell.png"
PROJECT = ROOT / "src/App/iPhoneMirror.App.csproj"
PROFILE = ROOT / "src/App/Services/DeviceFrameProfileResolver.cs"
EXPECTED_SHA256 = "0539067df832d4ec3da82baaf7e690ff51c584b7dae664e16062b236eea813b4"
EXPECTED_LENGTH = 317240


def verify() -> None:
    data = ASSET.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    assert len(data) == EXPECTED_LENGTH, f"Wrong shell byte length: {len(data)}"
    assert digest == EXPECTED_SHA256, f"Incorrect original shell SHA256: {digest}"
    assert data[:8] == b"\\x89PNG\\r\\n\\x1a\\n", "Missing PNG signature"
    assert data[12:16] == b"IHDR", "Missing PNG IHDR"
    width, height, depth, color_type = struct.unpack(">IIBB", data[16:26])
    assert (width, height, depth, color_type) == (994, 2048, 8, 6), (
        f"Expected 994x2048 8-bit RGBA, received {width}x{height} "
        f"depth={depth} type={color_type}"
    )

    project = PROJECT.read_text(encoding="utf-8")
    assert re.search(
        r'<EmbeddedResource\\s+Include="Assets\\\\iphone16_pro_max_shell.png"'
        r'\\s+LogicalName="IPhoneMirror.Shell.IPhone16ProMax"\\s*/>',
        project,
    ), "The original shell is no longer embedded in the WPF executable"

    profile = PROFILE.read_text(encoding="utf-8")
    match = re.search(
        r'IPhoneImportedShell\\s*=\\s*new\\s*\\(\\s*'
        r'"iphone-dynamic-island-custom-shell"\\s*,\\s*true\\s*,\\s*'
        r'([0-9.]+)\\s*,\\s*([0-9.]+)\\s*,\\s*([0-9.]+)',
        profile,
    )
    assert match, "Cannot locate the shared iPhone shell viewport profile"
    side, top, bottom = (float(x) for x in match.groups())
    # Original PNG central-alpha aperture approx x=43..949, y=39..2008.
    # These bounds are derived from the immutable original, not a fallback art.
    short = width / (1 + 2 * side)
    left = round(short * side)
    top_px = round(short * top)
    bottom_px = round(short * bottom)
    assert 43 <= left <= 45, f"Screen viewport left inset {left}px drifts from PNG"
    assert 38 <= top_px <= 40, f"Screen viewport top inset {top_px}px drifts from PNG"
    assert 38 <= bottom_px <= 40, f"Screen viewport bottom inset {bottom_px}px drifts from PNG"
    print(f"Verified pre6 original shell {width}x{height}, sha256={digest}")
    print(f"Shared viewport insets: left/right={left}px, top={top_px}px, bottom={bottom_px}px")
    print("NOTE: CI asset check does not establish visible or recorded frame alignment")


if __name__ == "__main__":
    try:
        verify()
    except (AssertionError, OSError, ValueError, struct.error) as exc:
        print(f"pre6 shell validation FAILED: {exc}", file=sys.stderr)
        sys.exit(1)
