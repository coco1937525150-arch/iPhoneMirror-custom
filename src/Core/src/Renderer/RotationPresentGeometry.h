#pragma once

#include <cstdint>

namespace iPhoneMirror::renderer {

// A square HWND is the INTENTIONAL temporary DirectComposition canvas: its
// video/shell swap-chain keeps portrait (or landscape) dimensions and may be
// rotated/scaled inside the square. A non-square HWND, in contrast, is the
// FINAL device body. Presenting the OLD buffer against it can expose a
// distorted bezel even when Present succeeds (observed in diagnostic trace).
//
// The UI moves its HWND on a different thread; the render-loop entry check
// is not sufficient. Call this immediately before the native Present.
[[nodiscard]] constexpr bool should_defer_phone_present(
    bool visual_rotation_active,
    std::uint32_t backbuffer_width, std::uint32_t backbuffer_height,
    long client_width, long client_height) noexcept {
    if (client_width <= 0 || client_height <= 0) return true;
    if (backbuffer_width == static_cast<std::uint32_t>(client_width) &&
        backbuffer_height == static_cast<std::uint32_t>(client_height))
        return false;
    // During the animation a square host is expected to differ from the
    // retained rectangular BackBuffer; no resize or blackout is needed.
    return !visual_rotation_active || client_width != client_height;
}

} // namespace iPhoneMirror::renderer
