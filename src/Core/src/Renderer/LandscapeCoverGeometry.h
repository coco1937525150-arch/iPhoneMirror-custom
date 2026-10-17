#pragma once
#include <algorithm>
#include <cmath>
namespace iPhoneMirror::renderer {
struct VideoCoverCrop { float x{1.0F}; float y{1.0F}; };
// Keep video pixels undistorted. For 16:9 content in a wider physical
// landscape iPhone aperture, crop the top/bottom instead of leaving side bars.
[[nodiscard]] inline VideoCoverCrop landscape_cover_crop(
    float source_aspect, float opening_aspect) noexcept {
    if (!std::isfinite(source_aspect) || !std::isfinite(opening_aspect) ||
        source_aspect <= 0.0F || opening_aspect <= 0.0F) return {};
    if (opening_aspect > source_aspect)
        return {1.0F, std::clamp(source_aspect / opening_aspect, 0.01F, 1.0F)};
    return {std::clamp(opening_aspect / source_aspect, 0.01F, 1.0F), 1.0F};
}
} // namespace iPhoneMirror::renderer
