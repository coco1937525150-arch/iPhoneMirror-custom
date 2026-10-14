#!/usr/bin/env python3
"""Compile the actual D3D11 preview shader string using Windows D3DCompiler.

Compiling D3D11PreviewRenderer.cpp does not compile its HLSL raw string:
shader errors otherwise surface only when a user opens a native preview.
"""
from __future__ import annotations

import ctypes
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SHADER_FILE = ROOT / "src/Core/src/Renderer/D3D11PreviewRenderer.cpp"
ENTRYPOINTS = ("nv12Main", "copyMain", "frameMain", "maskMain",
               "referenceShellMain", "cutoutMain")


def blob_bytes(ptr: ctypes.c_void_p) -> bytes:
    vtable = ctypes.cast(ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    get_ptr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vtable[3])
    get_size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vtable[4])
    return ctypes.string_at(get_ptr(ptr), get_size(ptr))


def release_blob(ptr: ctypes.c_void_p) -> None:
    vtable = ctypes.cast(ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtable[2])
    release(ptr)


def main() -> None:
    if not sys.platform.startswith("win"):
        raise SystemExit("D3D11 shader smoke test requires Windows")
    source = SHADER_FILE.read_text(encoding="utf-8")
    match = re.search(r'constexpr const char\* PreviewPixelShaders = R"\((.*?)\)";',
                      source, re.S)
    if not match:
        raise SystemExit("Could not extract production HLSL")
    blob = match.group(1).encode("utf-8")
    buf = ctypes.create_string_buffer(blob)
    compile_shader = ctypes.WinDLL("d3dcompiler_47.dll").D3DCompile
    compile_shader.argtypes = [
        ctypes.c_void_p, ctypes.c_size_t, ctypes.c_char_p,
        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_char_p,
        ctypes.c_char_p, ctypes.c_uint, ctypes.c_uint,
        ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
    compile_shader.restype = ctypes.c_long
    failures = []
    for entry in ENTRYPOINTS:
        compiled = ctypes.c_void_p()
        errors = ctypes.c_void_p()
        try:
            hr = compile_shader(buf, len(blob), b"iPhoneMirrorPre6", None, None,
                                entry.encode("ascii"), b"ps_5_0", 0, 0,
                                ctypes.byref(compiled), ctypes.byref(errors))
            if hr < 0:
                message = (blob_bytes(errors).decode("utf-8", "replace")
                           if errors.value else "")
                failures.append((entry, f"HRESULT={hr & 0xFFFFFFFF:08x}: {message}"))
            else:
                print(f"PASS {entry}: {len(blob_bytes(compiled))} bytes")
        finally:
            if compiled.value:
                release_blob(compiled)
            if errors.value:
                release_blob(errors)
    for name, error in failures:
        print(f"FAIL {name}: {error}", file=sys.stderr)
    if failures:
        raise SystemExit(1)
    print(f"Compiled {len(ENTRYPOINTS)} production preview shaders successfully")


if __name__ == "__main__":
    main()
