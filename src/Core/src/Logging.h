#pragma once

#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>

namespace iPhoneMirror::logging {

// Log levels and categories are kept at the native boundary so every
// subsystem can add structured context without duplicating timestamp/thread
// formatting. The legacy write(message) overload remains the default path.
enum class Level {
    Trace,
    Debug,
    Info,
    Warning,
    Error,
};

void initialize();
void write(std::string_view message) noexcept;
void write(Level level, std::string_view category, std::string_view message) noexcept;
void write_event(Level level, std::string_view category, std::string_view event,
    std::string_view details = {}) noexcept;

// The optional flash-diagnostics timeline uses the same native logging
// lifetime. Producers only copy fixed-size records into a bounded ring; a
// background worker writes JSONL. Values have event-specific names in the
// resulting file, and all timestamps use QueryPerformanceCounter.
enum class DiagnosticEvent : std::uint16_t {
    SessionStart = 1, SessionStop, OrientationDetected, OrientationConfirmed,
    RotationStarted, RotationProgress, RotationRetargeted, RotationCompleted,
    RotationAborted, HwndResize, SourceFrameReceived, SourceFrameDecoded,
    SourceInterrupted, SourceResumed, SourceFrameDropped, FrameSubmitted,
    FramePresented, FrameDropped, VisualCreated, VisualDestroyed,
    VisualRequested, VisualSetTransform, VisualCommit, SwapChainCreated,
    SwapChainDestroyed, TargetCreated, TargetReleased, ResizeBegin,
    ResizeEnd, DeviceRebuildBegin, DeviceRebuildEnd, DeviceLost,
    PresentBegin, PresentEnd, OutputModeChanged, GpuTextureRebuilt,
    FrameGap, DiagnosticOverflow,
};

[[nodiscard]] bool start_diagnostics(std::string_view id,
    const std::filesystem::path& timeline_path) noexcept;
[[nodiscard]] bool stop_diagnostics() noexcept;
[[nodiscard]] bool diagnostics_active() noexcept;
void set_diagnostic_context(std::uint64_t rotation_id,
    std::uint64_t orientation_event_id) noexcept;
void diagnostic_event(DiagnosticEvent event, std::uint64_t frame_id = 0,
    std::uint64_t visual_generation = 0, std::int64_t value0 = 0,
    std::int64_t value1 = 0, std::int64_t value2 = 0,
    std::int64_t value3 = 0, std::int32_t hresult = 0) noexcept;
// Produces a process-stable, salted SHA-256 label. The random salt changes on
// every launch so identifiers can be correlated within one diagnostic session
// without making logs a cross-session device tracking record.
[[nodiscard]] std::string fingerprint(std::string_view value) noexcept;
void shutdown();

} // namespace iPhoneMirror::logging
