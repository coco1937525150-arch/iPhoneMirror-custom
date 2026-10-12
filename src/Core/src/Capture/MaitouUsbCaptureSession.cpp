#include "Capture/MaitouUsbCaptureSession.h"
#include "Device/AppleUsbDiscovery.h"
#include "Logging.h"

#include <Windows.h>
#include <sddl.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <format>
#include <stdexcept>
#include <string_view>
#include <vector>

namespace iPhoneMirror::capture {
namespace {
using Microsoft::WRL::ComPtr;
using namespace std::chrono_literals;
constexpr wchar_t PipeName[] = LR"(\\.\pipe\Maitou.CaptureHost.v3)";
constexpr std::uint32_t PreviewMagic = 0x4D545054;
constexpr std::uint32_t PreviewVersion = 2;
constexpr UINT TextureExtent = 1600;
// Upstream CaptureHost has one global v3 control pipe and one per-user DXGI
// texture. A second concurrent USB session would read the *first* phone's
// frames. Until the upstream IPC becomes device-scoped, fail closed instead.
std::atomic_bool usb_host_lease{false};

#pragma pack(push, 8)
struct PreviewState {
    std::uint32_t magic, version, structure_size, reserved;
    volatile LONG running, generation, width, height;
    volatile LONG64 frame_number, last_frame_qpc, qpc_frequency, presentation_time_100ns;
    volatile LONG adapter_low, adapter_high;
};
#pragma pack(pop)

std::wstring user_sid() {
    HANDLE raw_token{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw_token))
        throw std::runtime_error("Maitou preview SID: OpenProcessToken failed");
    const auto close = [&] { CloseHandle(raw_token); };
    DWORD required{};
    GetTokenInformation(raw_token, TokenUser, nullptr, 0, &required);
    std::vector<std::byte> buffer(required);
    if (!required || !GetTokenInformation(raw_token, TokenUser,
            buffer.data(), required, &required)) {
        close();
        throw std::runtime_error("Maitou preview SID: GetTokenInformation failed");
    }
    const auto* token_user = reinterpret_cast<const TOKEN_USER*>(buffer.data());
    LPWSTR sid{};
    if (!ConvertSidToStringSidW(token_user->User.Sid, &sid)) {
        close();
        throw std::runtime_error("Maitou preview SID: conversion failed");
    }
    std::wstring result(sid);
    LocalFree(sid);
    close();
    return result;
}

std::filesystem::path host_executable() {
    std::wstring executable(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, executable.data(),
        static_cast<DWORD>(executable.size()));
    if (length == 0 || length >= executable.size())
        throw std::runtime_error("Cannot resolve iPhoneMirror executable path");
    executable.resize(length);
    return std::filesystem::path(executable).parent_path() /
        L"tools" / L"MaitouUsbCapture" / L"Maitou.CaptureHost.exe";
}

bool read_line(HANDLE pipe, std::string& line) {
    line.clear();
    char ch{};
    DWORD read{};
    for (std::size_t i{}; i < 16384; ++i) {
        if (!ReadFile(pipe, &ch, 1, &read, nullptr) || read != 1) return false;
        if (ch == '\n') return true;
        if (ch != '\r') line += ch;
    }
    return false;
}

std::wstring extract_json_string(std::string_view json, std::string_view key) {
    const std::string marker = std::format("\"{}\":\"", key);
    const auto start = json.find(marker);
    if (start == std::string_view::npos) return {};
    auto end = json.find('"', start + marker.length());
    if (end == std::string_view::npos) return {};
    const auto value = json.substr(start + marker.length(),
        end - start - marker.length());
    return std::wstring(value.begin(), value.end()); // Error codes are ASCII.
}

bool accepted(std::string_view json) {
    return json.find("\"accepted\":true") != std::string_view::npos &&
        json.find("\"schemaVersion\":3") != std::string_view::npos;
}

ComPtr<IDXGIAdapter1> find_adapter(const LUID luid) {
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return {};
    for (UINT index{};; ++index) {
        ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(index, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        if (!adapter) break;
        DXGI_ADAPTER_DESC1 desc{};
        if (SUCCEEDED(adapter->GetDesc1(&desc)) &&
            desc.AdapterLuid.LowPart == luid.LowPart &&
            desc.AdapterLuid.HighPart == luid.HighPart) return adapter;
    }
    return {};
}

class FrameReader final {
public:
    FrameReader() = default;
    ~FrameReader() {
        release_gpu();
        if (mapped_) UnmapViewOfFile(mapped_);
        if (mapping_) CloseHandle(mapping_);
    }
    FrameReader(const FrameReader&) = delete;
    FrameReader& operator=(const FrameReader&) = delete;

    bool attach(const std::wstring& sid) {
        if (mapped_) return true;
        const auto state_name = L"Local\\Maitou.PreviewState.v2." + sid;
        mapping_ = OpenFileMappingW(FILE_MAP_READ, FALSE, state_name.c_str());
        if (!mapping_) return false;
        mapped_ = static_cast<const PreviewState*>(
            MapViewOfFile(mapping_, FILE_MAP_READ, 0, 0, sizeof(PreviewState)));
        if (!mapped_) {
            CloseHandle(mapping_);
            mapping_ = nullptr;
            return false;
        }
        return true;
    }

    std::shared_ptr<media::DecodedFrame> read(const std::wstring& sid,
        std::uint64_t& sequence) {
        if (!mapped_ || mapped_->magic != PreviewMagic ||
            mapped_->version != PreviewVersion ||
            mapped_->structure_size != sizeof(PreviewState)) return {};
        if (mapped_->running == 0) {
            // Maitou closes and recreates the state mapping during a USB
            // unplug/reconnect. Drop our mapping and shared GPU handles now;
            // otherwise we could read an orphaned, permanently black view.
            release_gpu();
            UnmapViewOfFile(mapped_);
            mapped_ = nullptr;
            CloseHandle(mapping_);
            mapping_ = nullptr;
            generation_ = 0;
            sequence = 0;
            return {};
        }
        const auto generation = static_cast<std::uint32_t>(mapped_->generation);
        if (generation != generation_) {
            generation_ = generation;
            sequence = 0;
            release_gpu();
        }
        const auto width = mapped_->width;
        const auto height = mapped_->height;
        if (width < 2 || height < 2 || width > static_cast<LONG>(TextureExtent) ||
            height > static_cast<LONG>(TextureExtent) ||
            (width & 1) || (height & 1)) return {};
        const auto number = static_cast<std::uint64_t>(mapped_->frame_number);
        if (number == 0 || number == sequence) return {};
        if (!source_ && !connect_gpu(sid)) return {};
        if (!mutex_ || mutex_->AcquireSync(1, 3) != S_OK) return {};
        context_->CopyResource(staging_.Get(), source_.Get());
        mutex_->ReleaseSync(0);

        D3D11_MAPPED_SUBRESOURCE region{};
        if (FAILED(context_->Map(staging_.Get(), 0, D3D11_MAP_READ, 0,
                &region))) return {};
        auto result = std::make_shared<media::DecodedFrame>();
        result->width = static_cast<std::uint32_t>(width);
        result->height = static_cast<std::uint32_t>(height);
        result->stride = width;
        result->pixel_format = media::PixelFormat::Nv12;
        result->color.primaries = coremedia::ColorPrimaries::Bt709;
        result->color.transfer = coremedia::TransferFunction::Bt709;
        result->color.matrix = coremedia::MatrixCoefficients::Bt709;
        result->color.range = coremedia::ColorRange::Limited;
        result->timestamp_100ns = mapped_->presentation_time_100ns;
        result->received_at = std::chrono::steady_clock::now();
        const auto w = static_cast<std::size_t>(width);
        const auto h = static_cast<std::size_t>(height);
        result->nv12.resize(w * h * 3 / 2);
        const auto clamp8 = [](int value) -> std::uint8_t {
            return static_cast<std::uint8_t>(std::clamp(value, 0, 255));
        };
        // BGRA8 -> BT.709 limited-range NV12; process contiguous chroma
        // samples by averaging a 2x2 RGB block to avoid chroma aliasing.
        for (std::size_t y{}; y < h; ++y) {
            const auto* row = static_cast<const std::uint8_t*>(region.pData) +
                y * region.RowPitch;
            for (std::size_t x{}; x < w; ++x) {
                const auto* px = row + x * 4;
                const int b = px[0], g = px[1], r = px[2];
                result->nv12[y * w + x] = clamp8(
                    (47 * r + 157 * g + 16 * b + 128) / 256 + 16);
            }
        }
        for (std::size_t y{}; y < h; y += 2) {
            for (std::size_t x{}; x < w; x += 2) {
                int r{}, g{}, b{};
                for (std::size_t dy{}; dy < 2; ++dy) {
                    const auto* row = static_cast<const std::uint8_t*>(region.pData) +
                        (y + dy) * region.RowPitch;
                    for (std::size_t dx{}; dx < 2; ++dx) {
                        const auto* px = row + (x + dx) * 4;
                        b += px[0]; g += px[1]; r += px[2];
                    }
                }
                r = (r + 2) / 4; g = (g + 2) / 4; b = (b + 2) / 4;
                const auto uv = w * h + y / 2 * w + x;
                result->nv12[uv] = clamp8((-26 * r - 87 * g + 113 * b + 128) / 256 + 128);
                result->nv12[uv + 1] = clamp8((113 * r - 103 * g - 10 * b + 128) / 256 + 128);
            }
        }
        context_->Unmap(staging_.Get(), 0);
        sequence = number;
        return result;
    }

private:
    bool connect_gpu(const std::wstring& sid) {
        if (!mapped_) return false;
        const LUID luid{
            static_cast<DWORD>(mapped_->adapter_low),
            static_cast<LONG>(mapped_->adapter_high)
        };
        auto adapter = find_adapter(luid);
        if (!adapter) return false;
        D3D_FEATURE_LEVEL feature_level{};
        if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN,
                nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr, 0, D3D11_SDK_VERSION,
                &device_, &feature_level, &context_))) return false;
        ComPtr<ID3D11Device1> extended;
        if (FAILED(device_.As(&extended))) { release_gpu(); return false; }
        const auto tex_name = L"Local\\Maitou.PreviewTexture.v2." + sid;
        if (FAILED(extended->OpenSharedResourceByName(tex_name.c_str(),
                DXGI_SHARED_RESOURCE_READ, IID_PPV_ARGS(&source_))) ||
            FAILED(source_.As(&mutex_))) { release_gpu(); return false; }
        D3D11_TEXTURE2D_DESC staging_desc{};
        source_->GetDesc(&staging_desc);
        if (staging_desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
            staging_desc.Width != TextureExtent ||
            staging_desc.Height != TextureExtent) {
            release_gpu();
            return false;
        }
        staging_desc.Usage = D3D11_USAGE_STAGING;
        staging_desc.BindFlags = 0;
        staging_desc.MiscFlags = 0;
        staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(device_->CreateTexture2D(&staging_desc, nullptr, &staging_))) {
            release_gpu();
            return false;
        }
        return true;
    }

    void release_gpu() {
        staging_.Reset();
        mutex_.Reset();
        source_.Reset();
        context_.Reset();
        device_.Reset();
    }

    HANDLE mapping_{};
    const PreviewState* mapped_{};
    std::uint32_t generation_{};
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<ID3D11Texture2D> source_;
    ComPtr<IDXGIKeyedMutex> mutex_;
    ComPtr<ID3D11Texture2D> staging_;
};
} // namespace

MaitouUsbCaptureSession::MaitouUsbCaptureSession(std::string selected_udid,
    CapturePreferences preferences)
    : selected_udid_(std::move(selected_udid)), preferences_(preferences),
      fps_limit_(std::max(1U, preferences.target_fps)),
      requested_decoder_(static_cast<std::uint32_t>(preferences.decoder_preference)) {
    status_.state = State::Idle;
    status_.message = L"Maitou USB backend idle";
}

MaitouUsbCaptureSession::~MaitouUsbCaptureSession() { stop(); }

bool MaitouUsbCaptureSession::command(const char* operation, std::string& response) {
    if (!pipe_ || pipe_ == INVALID_HANDLE_VALUE) return false;
    static std::atomic_uint64_t counter{};
    const auto nonce = (static_cast<std::uint64_t>(GetCurrentProcessId()) << 32) ^
        (++counter + GetTickCount64());
    const std::string request = std::format("3|{:032x}|{}\n", nonce, operation);
    DWORD written{};
    if (!WriteFile(pipe_, request.data(), static_cast<DWORD>(request.size()),
            &written, nullptr) || written != request.size()) return false;
    return read_line(pipe_, response);
}

void MaitouUsbCaptureSession::set_status(State state, std::wstring message) {
    std::scoped_lock lock(mutex_);
    status_.state = state;
    status_.message = std::move(message);
}

void MaitouUsbCaptureSession::fail(FailureKind kind, FailureStage stage,
    std::wstring message) {
    std::scoped_lock lock(mutex_);
    status_.state = State::Error;
    status_.failure_kind = kind;
    status_.failure_stage = stage;
    status_.message = std::move(message);
}

void MaitouUsbCaptureSession::start() try {
    if (process_ || pipe_ || reader_.joinable())
        throw std::runtime_error("Maitou USB capture already active");
    const auto path = host_executable();
    if (!std::filesystem::is_regular_file(path))
        throw std::runtime_error("Maitou USB host missing; reinstall full portable build");
    const auto connected = device::discover_physical_apple_usb_devices();
    if (connected.size() > 1)
        throw std::runtime_error(
            "Maitou USB backend is single-device only; unplug extra iPhones before capture");
    bool expected = false;
    if (!usb_host_lease.compare_exchange_strong(expected, true,
            std::memory_order_acq_rel))
        throw std::runtime_error(
            "Maitou USB video capture already active for another device");
    owns_usb_host_lease_ = true;
    logging::write(std::format(
        "maitou_usb requested_device_fp={} physical_apple_candidates={}",
        logging::fingerprint(selected_udid_), connected.size()));
    // CaptureHost verifies that the connecting pipe client's PID is exactly
    // this GUI's PID. Driver installation or filters are NEVER performed.
    auto args = std::format(L"\"{}\" --parent-pid {}", path.wstring(),
        GetCurrentProcessId());
    STARTUPINFOW startup{.cb = sizeof(startup)};
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(path.c_str(), args.data(), nullptr, nullptr,
            FALSE, CREATE_NO_WINDOW, nullptr, path.parent_path().c_str(),
            &startup, &child))
        throw std::runtime_error("Could not start Maitou USB capture host");
    CloseHandle(child.hThread);
    process_ = child.hProcess;
    const auto deadline = std::chrono::steady_clock::now() + 12s;
    do {
        if (WaitForSingleObject(process_, 0) == WAIT_OBJECT_0) break;
        pipe_ = CreateFileW(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (pipe_ != INVALID_HANDLE_VALUE) {
            ULONG host_pid{};
            if (GetNamedPipeServerProcessId(pipe_, &host_pid) &&
                host_pid == child.dwProcessId) break;
            CloseHandle(pipe_);
        }
        pipe_ = nullptr;
        std::this_thread::sleep_for(100ms);
    } while (std::chrono::steady_clock::now() < deadline);
    if (!pipe_ || pipe_ == INVALID_HANDLE_VALUE) {
        stop();
        throw std::runtime_error("Maitou USB host failed to open authenticated control pipe");
    }
    std::string reply;
    if (!command("StartAutoCapture", reply) || !accepted(reply)) {
        const auto reason = extract_json_string(reply, "errorCode");
        stop();
        throw std::runtime_error(std::format(
            "Maitou USB capture rejected startup: {}",
            std::string(reason.begin(), reason.end())));
    }
    const auto initial_state = extract_json_string(reply, "state");
    if (initial_state == L"DriverRequired" || initial_state == L"Error") {
        const auto reason = extract_json_string(reply, "errorCode");
        throw std::runtime_error(std::format(
            "Maitou USB host refused the selected Windows driver: {}",
            std::string(reason.begin(), reason.end())));
    }
    set_status(State::ActivatingUsb, L"Maitou USB capture: connecting");
    reader_ = std::jthread([this](std::stop_token stop) { read_loop(stop); });
    logging::write("usb_capture_backend=maitou_external_v3");
} catch (...) {
    stop();
    throw;
}

void MaitouUsbCaptureSession::read_loop(std::stop_token stop) noexcept {
    try {
        const auto sid = user_sid();
        FrameReader input;
        std::uint64_t sequence{};
        const auto started = std::chrono::steady_clock::now();
        auto first_frame_deadline_origin = started;
        auto last_status = started - 2s;
        auto last_frame = started;
        auto fps_start = started;
        std::uint32_t recent_frames{};
        bool source_unavailable{};
        while (!stop.stop_requested()) {
            if (WaitForSingleObject(process_, 0) == WAIT_OBJECT_0) {
                fail(FailureKind::ChildProcessExited, FailureStage::VideoStream,
                    L"Maitou USB host exited; check driver and USB diagnostics");
                break;
            }
            const auto now = std::chrono::steady_clock::now();
            if (now - last_status >= 1s) {
                last_status = now;
                std::string status_reply;
                if (!command("GetStatus", status_reply) || !accepted(status_reply)) {
                    fail(FailureKind::ChildProcessExited, FailureStage::VideoStream,
                        L"Lost Maitou USB control pipe");
                    break;
                }
                const auto state = extract_json_string(status_reply, "state");
                source_unavailable = state == L"NoDevice" ||
                    state == L"DeviceDisconnected" ||
                    state == L"Recovering" || state == L"TrustRequired";
                if (source_unavailable) {
                    // Never keep the previous phone's image published while
                    // iOS is disconnected or waiting for trust. Maitou keeps
                    // its own auto-reconnect worker armed; we only pause.
                    std::scoped_lock lock(mutex_);
                    latest_.reset();
                    consumed_sequence_ = published_sequence_;
                    status_.width = status_.height = 0;
                    status_.fps = 0;
                    status_.state = State::WaitingForDevice;
                    status_.message = state == L"TrustRequired"
                        ? L"Unlock iPhone and trust this computer"
                        : L"USB disconnected; waiting for iPhone reconnect";
                    last_frame = now;
                    // Re-arm first-frame timeout on each reconnect instead
                    // of immediately failing when the session has already
                    // existed for more than thirty seconds.
                    first_frame_deadline_origin = now;
                }
                if (state == L"DriverRequired") {
                    fail(FailureKind::Driver, FailureStage::UsbPreflight,
                        L"Maitou requires an approved libusb0/Apple USB driver. No driver was modified.");
                    break;
                }
                if (state == L"Error") {
                    fail(FailureKind::UsbConnection, FailureStage::UsbActivation,
                        L"Maitou USB error: " + extract_json_string(status_reply, "errorCode"));
                    break;
                } else if (!latest_frame())
                    set_status(State::WaitingForDevice, L"Maitou USB: waiting for first video frame");
            }
            if (input.attach(sid)) {
                if (auto frame = input.read(sid, sequence)) {
                    std::scoped_lock lock(mutex_);
                    latest_ = frame;
                    status_.state = State::Streaming;
                    status_.message = L"Maitou USB video capture";
                    status_.width = frame->width;
                    status_.height = frame->height;
                    status_.video_frames++;
                    published_sequence_++;
                    recent_frames++;
                    last_frame = now;
                }
            }
            if (now - fps_start >= 1s) {
                const auto elapsed = std::chrono::duration<double>(now - fps_start).count();
                std::scoped_lock lock(mutex_);
                status_.fps = recent_frames / std::max(elapsed, 0.001);
                recent_frames = 0;
                fps_start = now;
            }
            if (!source_unavailable &&
                now - first_frame_deadline_origin > 30s && !latest_frame()) {
                fail(FailureKind::NoVideoFrames, FailureStage::VideoStream,
                    L"Maitou USB did not deliver a decoded frame within 30 seconds");
                break;
            }
            if (!source_unavailable && latest_frame() && now - last_frame > 12s) {
                fail(FailureKind::NoVideoFrames, FailureStage::VideoStream,
                    L"Maitou USB stopped publishing video frames");
                break;
            }
            std::this_thread::sleep_for(25ms);
        }
    } catch (...) {
        fail(FailureKind::VideoStream, FailureStage::Decoder,
            L"Maitou USB DXGI frame import failed");
    }
}

void MaitouUsbCaptureSession::stop() noexcept {
    if (reader_.joinable()) {
        reader_.request_stop();
        // Polling uses synchronous named-pipe I/O. Request cancellation to
        // avoid waiting indefinitely for GetStatus on disconnect or shutdown.
        (void)CancelSynchronousIo(reader_.native_handle());
        reader_.join();
    }
    if (pipe_ && pipe_ != INVALID_HANDLE_VALUE) {
        std::string reply;
        (void)command("ShutdownAndRestoreDevice", reply);
        CloseHandle(pipe_);
        pipe_ = nullptr;
    }
    if (process_) {
        // USB restore may take tens of seconds. Do not terminate a live
        // configuration transition while the host is restoring the iPhone.
        const auto result = WaitForSingleObject(process_, 45000);
        if (result != WAIT_OBJECT_0)
            logging::write(logging::Level::Warning, "maitou_usb",
                "CaptureHost did not exit after bounded device restore");
        CloseHandle(process_);
        process_ = nullptr;
    }
    std::scoped_lock lock(mutex_);
    latest_.reset();
    published_sequence_ = consumed_sequence_ = 0;
    status_ = Snapshot{};
    status_.state = State::Stopped;
    status_.message = L"Maitou USB stopped";
    if (owns_usb_host_lease_) {
        owns_usb_host_lease_ = false;
        usb_host_lease.store(false, std::memory_order_release);
    }
}

Snapshot MaitouUsbCaptureSession::snapshot() const {
    std::scoped_lock lock(mutex_);
    return status_;
}

std::int64_t MaitouUsbCaptureSession::latest_frame_timestamp() const {
    std::scoped_lock lock(mutex_);
    return latest_ ? latest_->timestamp_100ns : 0;
}

std::shared_ptr<const media::DecodedFrame> MaitouUsbCaptureSession::latest_frame() const {
    std::scoped_lock lock(mutex_);
    return latest_;
}

std::shared_ptr<const media::DecodedFrame> MaitouUsbCaptureSession::next_render_frame() {
    std::scoped_lock lock(mutex_);
    if (published_sequence_ == consumed_sequence_) return {};
    consumed_sequence_ = published_sequence_;
    return latest_;
}

std::shared_ptr<const AudioPacket>
MaitouUsbCaptureSession::next_audio_packet(std::uint64_t) const { return {}; }
void MaitouUsbCaptureSession::set_audio_enabled(bool) noexcept {}
void MaitouUsbCaptureSession::set_audio_volume(float) noexcept {}
void MaitouUsbCaptureSession::set_target_fps(std::uint32_t fps) noexcept {
    fps_limit_.store(std::clamp(fps, 1U, 60U));
}
std::uint32_t MaitouUsbCaptureSession::target_fps() const noexcept {
    return fps_limit_.load();
}
void MaitouUsbCaptureSession::set_decoder_preference(media::DecoderPreference value) noexcept {
    requested_decoder_.store(static_cast<std::uint32_t>(value));
}
DecoderSwitchStatus MaitouUsbCaptureSession::decoder_switch_status() const noexcept {
    const auto decoder = static_cast<media::DecoderPreference>(requested_decoder_.load());
    return {decoder, decoder, 1, 1, DecoderSwitchPhase::Applied,
        DecoderRuntimeMode::External};
}
void MaitouUsbCaptureSession::request_display_orientation(bool) noexcept {}
} // namespace iPhoneMirror::capture
