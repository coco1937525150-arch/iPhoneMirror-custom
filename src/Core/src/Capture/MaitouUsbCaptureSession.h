#pragma once

#include "Capture/ICaptureSession.h"
#include <Windows.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

namespace iPhoneMirror::capture {

// A separate USB video backend. Maitou owns its own QuickTime USB lifecycle,
// driver preflight, H.264 decoder and device restoration. This class consumes
// only the verified per-user DXGI preview; the legacy CaptureSession is NEVER
// started by this backend. Bluetooth / Rust HID control remains independent.
class MaitouUsbCaptureSession final : public ICaptureSession {
public:
    explicit MaitouUsbCaptureSession(std::string selected_udid,
        CapturePreferences preferences);
    ~MaitouUsbCaptureSession() override;

    void start();
    void stop() noexcept override;
    [[nodiscard]] Snapshot snapshot() const override;
    [[nodiscard]] std::int64_t latest_frame_timestamp() const override;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> latest_frame() const override;
    [[nodiscard]] std::shared_ptr<const media::DecodedFrame> next_render_frame() override;
    [[nodiscard]] std::shared_ptr<const AudioPacket> next_audio_packet(
        std::uint64_t after_sequence) const override;
    void set_audio_enabled(bool enabled) noexcept override;
    void set_audio_volume(float volume) noexcept override;
    void set_target_fps(std::uint32_t target_fps) noexcept override;
    [[nodiscard]] std::uint32_t target_fps() const noexcept override;
    void set_decoder_preference(media::DecoderPreference preference) noexcept override;
    [[nodiscard]] DecoderSwitchStatus decoder_switch_status() const noexcept override;
    void request_display_orientation(bool landscape) noexcept override;

private:
    std::string selected_udid_;
    CapturePreferences preferences_;
    mutable std::mutex mutex_;
    Snapshot status_;
    std::shared_ptr<const media::DecodedFrame> latest_;
    std::uint64_t published_sequence_{};
    std::uint64_t consumed_sequence_{};
    HANDLE process_{};
    HANDLE pipe_{};
    bool owns_usb_host_lease_{};
    std::jthread reader_;
    std::atomic_uint32_t fps_limit_{60};
    std::atomic_uint32_t requested_decoder_{0};

    bool command(const char* operation, std::string& response);
    void set_status(State state, std::wstring message);
    void fail(FailureKind kind, FailureStage stage, std::wstring message);
    void read_loop(std::stop_token stop) noexcept;
};
} // namespace iPhoneMirror::capture
