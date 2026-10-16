#include "Logging.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <format>
#include <iomanip>
#include <limits>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>

namespace iPhoneMirror::logging {
namespace {

std::mutex log_mutex;
std::mutex lifecycle_mutex;
std::ofstream log_file;
std::filesystem::path log_path;
bool initialized{};
bool writes_suspended{};
bool session_header_written{};
std::size_t pending_lines{};
std::chrono::steady_clock::time_point last_flush{};
std::jthread flush_thread;
std::uint64_t sequence{};
std::uint64_t line_count{};
std::uint64_t warning_count{};
std::uint64_t error_count{};
std::uint64_t byte_count{};
std::uint64_t dropped_write_count{};
std::string session_id;
std::chrono::steady_clock::time_point session_started{};
std::once_flag fingerprint_salt_once;
std::array<unsigned char, 32> fingerprint_salt{};
bool fingerprint_salt_ready{};

struct TraceRecord {
    DiagnosticEvent event{};
    std::uint64_t frame_id{};
    std::uint64_t visual_generation{};
    std::uint64_t rotation_id{};
    std::uint64_t orientation_event_id{};
    std::int64_t values[4]{};
    std::int64_t qpc_ticks{};
    std::uint32_t thread_id{};
    std::int32_t hresult{};
};

struct TraceDescriptor {
    const char* name;
    std::array<const char*, 4> keys;
};

constexpr std::array<TraceDescriptor, 50> TraceEvents{{
    {"unknown", {"value0", "value1", "value2", "value3"}},
    {"diagnostic_start", {"qpc_frequency", "utc_filetime_100ns", "", ""}},
    {"diagnostic_stop", {"ring_events_lost", "", "", ""}},
    {"orientation_detected", {"width", "height", "landscape", ""}},
    {"orientation_confirmed", {"width", "height", "landscape", "confirmation_ms"}},
    {"rotation_started", {"from_angle_mdeg", "target_angle_mdeg", "client_width", "client_height"}},
    {"rotation_progress", {"angle_mdeg", "target_angle_mdeg", "scale_ppm", "elapsed_ms"}},
    {"rotation_retargeted", {"from_angle_mdeg", "target_angle_mdeg", "", ""}},
    {"rotation_completed", {"quarter_turns", "client_width", "client_height", "elapsed_ms"}},
    {"rotation_aborted", {"client_width", "client_height", "", ""}},
    {"hwnd_resize", {"client_width", "client_height", "outer_width", "outer_height"}},
    {"source_frame_received", {"source_width", "source_height", "source_timestamp_100ns", ""}},
    {"source_frame_decoded", {"source_width", "source_height", "source_timestamp_100ns", "decode_us"}},
    {"source_interrupted", {"reason", "", "", ""}},
    {"source_resumed", {"width", "height", "", ""}},
    {"source_frame_dropped", {"reason", "width", "height", ""}},
    {"frame_submitted", {"source_timestamp_100ns", "logical_quarter_turns", "reused_old_frame", ""}},
    {"frame_presented", {"source_timestamp_100ns", "backbuffer_width", "backbuffer_height", ""}},
    {"frame_dropped", {"reason", "source_timestamp_100ns", "", ""}},
    {"visual_created", {"client_width", "client_height", "", ""}},
    {"visual_destroyed", {"", "", "", ""}},
    {"visual_requested", {"angle_mdeg", "scale_ppm", "canvas_width", "canvas_height"}},
    {"visual_set_transform", {"m11_ppm", "m12_ppm", "m21_ppm", "m22_ppm"}},
    {"visual_commit", {"translate_x_milli", "translate_y_milli", "angle_mdeg", "scale_ppm"}},
    {"swapchain_created", {"width", "height", "dxgi_format", ""}},
    {"swapchain_destroyed", {"", "", "", ""}},
    {"target_created", {"width", "height", "dxgi_format", ""}},
    {"target_released", {"width", "height", "", ""}},
    {"resize_begin", {"width", "height", "old_width", "old_height"}},
    {"resize_end", {"width", "height", "dxgi_format", "duration_us"}},
    {"device_rebuild_begin", {"reason", "", "", ""}},
    {"device_rebuild_end", {"duration_us", "", "", ""}},
    {"device_lost", {"reason", "", "", ""}},
    {"present_begin", {"source_timestamp_100ns", "backbuffer_width", "backbuffer_height", "handoff"}},
    {"present_end", {"source_timestamp_100ns", "duration_us", "backbuffer_width", "backbuffer_height"}},
    {"output_mode_changed", {"hdr", "dxgi_format", "color_space", ""}},
    {"gpu_texture_rebuilt", {"width", "height", "dxgi_format", "duration_us"}},
    {"frame_gap", {"interval_us", "source_timestamp_100ns", "", ""}},
    {"diagnostic_overflow", {"events_lost", "", "", ""}},
    {"recording_source_stall", {"source_age_ms", "requested_fps", "source_frame_timestamp_100ns", ""}},
    {"recording_frame_repeat", {"repeated_slots", "total_slots", "source_frame_timestamp_100ns", ""}},
    {"recording_encoder_stall", {"write_duration_ms", "requested_fps", "slots", ""}},
    {"recording_unstable_window", {"unique_fps_milli", "requested_fps", "repeated_slots", "max_write_ms"}},
    {"visible_flash_marked", {"marker_id", "lookback_ms", "lookahead_ms", ""}},
    {"frame_geometry", {"video_turns", "crop_x_ppm", "crop_y_ppm", "staging_turns"}},
    {"frame_aperture", {"opening_x", "opening_y", "opening_width", "opening_height"}},
    {"frame_viewport", {"viewport_x", "viewport_y", "viewport_width", "viewport_height"}},
    {"frame_client_geometry", {"client_width", "client_height", "backbuffer_width", "backbuffer_height"}},
    {"rotation_requested", {"delta", "trigger_auto", "committed_turns", "animating"}},
    {"rotation_trigger", {"trigger_auto", "orientation_event_id", "", ""}},
}};

constexpr std::size_t TraceCapacity = 8192;
constexpr std::streamoff TraceMaximumFileBytes = 128LL * 1024LL * 1024LL;
std::array<TraceRecord, TraceCapacity> trace_ring{};
std::mutex trace_mutex;
std::condition_variable trace_ready;
std::ofstream trace_file;
std::jthread trace_worker;
std::string trace_session_id;
std::size_t trace_head{};
std::size_t trace_count{};
std::uint64_t trace_lost{};
std::atomic_uint64_t trace_file_lost{};
std::atomic_bool trace_active{};
std::atomic_bool trace_writer_failed{};
std::atomic_uint64_t trace_rotation_id{};
std::atomic_uint64_t trace_orientation_id{};

TraceRecord make_trace_record(DiagnosticEvent event, std::uint64_t frame_id,
    std::uint64_t visual_generation, std::int64_t value0, std::int64_t value1,
    std::int64_t value2, std::int64_t value3, std::int32_t hresult) noexcept {
    TraceRecord record{};
    record.event = event;
    record.frame_id = frame_id;
    record.visual_generation = visual_generation;
    record.rotation_id = trace_rotation_id.load(std::memory_order_relaxed);
    record.orientation_event_id = trace_orientation_id.load(std::memory_order_relaxed);
    record.values[0] = value0;
    record.values[1] = value1;
    record.values[2] = value2;
    record.values[3] = value3;
    record.thread_id = GetCurrentThreadId();
    record.hresult = hresult;
    LARGE_INTEGER counter{};
    QueryPerformanceCounter(&counter);
    record.qpc_ticks = counter.QuadPart;
    return record;
}

void enqueue_trace_locked(const TraceRecord& record) noexcept {
    if (trace_count == TraceCapacity) {
        ++trace_lost;
        return;
    }
    trace_ring[(trace_head + trace_count) % TraceCapacity] = record;
    ++trace_count;
    trace_ready.notify_one();
}

void write_trace_record(const TraceRecord& record) {
    if (record.event != DiagnosticEvent::SessionStop &&
        record.event != DiagnosticEvent::DiagnosticOverflow &&
        static_cast<std::streamoff>(trace_file.tellp()) >= TraceMaximumFileBytes) {
        trace_file_lost.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    const auto index = static_cast<std::size_t>(record.event);
    const auto& descriptor = TraceEvents[index < TraceEvents.size() ? index : 0];
    trace_file << "{\"session_id\":\"" << trace_session_id
        << "\",\"event\":\"" << descriptor.name
        << "\",\"qpc_ticks\":" << record.qpc_ticks
        << ",\"thread_id\":" << record.thread_id
        << ",\"orientation_event_id\":" << record.orientation_event_id
        << ",\"rotation_id\":" << record.rotation_id
        << ",\"visual_generation\":" << record.visual_generation
        << ",\"frame_id\":" << record.frame_id
        << ",\"hresult\":" << record.hresult;
    for (std::size_t i = 0; i < descriptor.keys.size(); ++i) {
        if (descriptor.keys[i][0] != '\0')
            trace_file << ",\"" << descriptor.keys[i] << "\":" << record.values[i];
    }
    trace_file << "}\n";
    if (!trace_file.good()) trace_writer_failed.store(true, std::memory_order_release);
}

void run_trace_writer(std::stop_token token) noexcept {
    std::array<TraceRecord, 256> batch{};
    for (;;) {
        std::size_t count{};
        {
            std::unique_lock lock(trace_mutex);
            trace_ready.wait_for(lock, std::chrono::milliseconds(100), [&] {
                return trace_count != 0 || token.stop_requested();
            });
            while (count < batch.size() && trace_count != 0) {
                batch[count++] = trace_ring[trace_head];
                trace_head = (trace_head + 1) % TraceCapacity;
                --trace_count;
            }
            if (count == 0 && token.stop_requested()) break;
        }
        try {
            for (std::size_t i = 0; i < count; ++i) write_trace_record(batch[i]);
            if (count != 0) trace_file.flush();
            if (!trace_file.good()) trace_writer_failed.store(true, std::memory_order_release);
        } catch (...) {
            trace_writer_failed.store(true, std::memory_order_release);
        }
    }
}

void initialize_fingerprint_salt() noexcept {
    fingerprint_salt_ready = BCryptGenRandom(nullptr, fingerprint_salt.data(),
        static_cast<ULONG>(fingerprint_salt.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0;
}

const char* level_text(Level level) noexcept {
    switch (level) {
    case Level::Trace: return "TRACE";
    case Level::Debug: return "DEBUG";
    case Level::Info: return "INFO";
    case Level::Warning: return "WARN";
    case Level::Error: return "ERROR";
    }
    return "INFO";
}

std::string sanitize_single_line(std::string_view value) {
    constexpr std::size_t MaximumMessageBytes = 64U * 1024U;
    const auto length = std::min(value.size(), MaximumMessageBytes);
    std::string result;
    result.reserve(length);
    for (std::size_t index = 0; index < length; ++index) {
        const auto character = static_cast<unsigned char>(value[index]);
        if (character == '\r' || character == '\n' || character == '\t')
            result.push_back(' ');
        else if (character < 0x20 || character == 0x7f)
            result.push_back('?');
        else
            result.push_back(static_cast<char>(character));
    }
    if (value.size() > MaximumMessageBytes) result += "...<truncated>";
    return result;
}

std::string normalize_token(std::string_view value, std::string_view fallback) {
    constexpr std::size_t MaximumTokenBytes = 32;
    std::string result;
    result.reserve(std::min(value.size(), MaximumTokenBytes));
    for (const auto character : value) {
        if (result.size() >= MaximumTokenBytes) break;
        const auto byte = static_cast<unsigned char>(character);
        if ((byte >= 'a' && byte <= 'z') || (byte >= 'A' && byte <= 'Z') ||
            (byte >= '0' && byte <= '9') || byte == '_' || byte == '-')
            result.push_back(static_cast<char>(byte));
        else
            result.push_back('_');
    }
    if (result.empty()) result.assign(fallback);
    return result;
}

std::string infer_category(std::string_view message) {
    const auto first_separator = message.find_first_of(" :");
    const auto token = message.substr(0, first_separator);
    if (token == "ui_event" || token == "action" || token == "diagnostic")
        return "ui";
    const auto underscore = token.find('_');
    return normalize_token(token.substr(0, underscore), "general");
}

Level infer_level(std::string_view message) {
    if (message.starts_with("diagnostic ")) return Level::Debug;
    if (message.starts_with("error ") || message.starts_with("ERROR ") ||
        message.find(" failed") != std::string_view::npos ||
        message.find(" exception") != std::string_view::npos)
        return Level::Error;
    if (message.find(" rejected") != std::string_view::npos ||
        message.find(" dropped") != std::string_view::npos ||
        message.find(" unavailable") != std::string_view::npos)
        return Level::Warning;
    return Level::Info;
}

std::string make_session_id() {
    const auto ticks = static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count());
    return std::format("{:08X}-{:016X}", GetCurrentProcessId(), ticks);
}

std::string now_text();

void flush_pending_locked() {
    if (pending_lines == 0) return;
    const auto lines = pending_lines;
    if (!log_file.is_open() || !log_file.good()) {
        dropped_write_count += lines;
        pending_lines = 0;
        return;
    }
    log_file.flush();
    if (!log_file.good()) {
        dropped_write_count += lines;
    }
    pending_lines = 0;
    last_flush = std::chrono::steady_clock::now();
}

void write_line_locked(Level level, std::string_view category,
    std::string_view message) {
    if (!log_file.is_open() || !log_file.good()) {
        ++dropped_write_count;
        return;
    }
    const auto sanitized_category = normalize_token(category, "general");
    const auto sanitized_message = sanitize_single_line(message);
    const auto line_number = sequence + 1;
    std::ostringstream line;
    line << now_text() << " [tid="
        << std::hash<std::thread::id>{}(std::this_thread::get_id())
        << "] [seq=" << line_number << "] [level=" << level_text(level)
        << "] [category=" << sanitized_category << "] [session="
        << session_id << "] " << sanitized_message << '\n';
    const auto text = line.str();
    log_file << text;
    if (!log_file.good()) {
        // Do not call the logger recursively while handling a disk/handle
        // failure. Leave failbit set so the next logging call can reopen the
        // target, and carry the loss into that recovery marker.
        ++dropped_write_count;
        return;
    }
    sequence = line_number;
    ++line_count;
    if (level == Level::Warning) ++warning_count;
    if (level == Level::Error) ++error_count;
    byte_count += text.size();
    ++pending_lines;
    const auto now = std::chrono::steady_clock::now();
    if (pending_lines >= 64 || now - last_flush >= std::chrono::milliseconds(500)) {
        flush_pending_locked();
    }
}

void ensure_flush_thread_locked() {
    if (flush_thread.joinable()) return;
    flush_thread = std::jthread([](std::stop_token token) {
        // Keep the GUI log tail genuinely live even if the last error is the
        // final line written. Flushing on this background worker avoids disk
        // I/O in the USB/decode/render hot paths.
        while (!token.stop_requested()) {
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
            std::scoped_lock lock(log_mutex);
            flush_pending_locked();
        }
        std::scoped_lock lock(log_mutex);
        flush_pending_locked();
    });
}

std::filesystem::path default_path() {
    // TEMP is writable for both a normal desktop launch and a packaged EXE;
    // unlike the application directory it does not require elevation.
    return std::filesystem::temp_directory_path() / L"iPhoneMirror-capture.log";
}

std::filesystem::path configured_path() {
    constexpr auto variable = L"IPHONE_MIRROR_LOG_FILE";
    // Environment variables can approach 32 KiB. Allocate one buffer large enough
    // for the whole value and read it in a single call to avoid the TOCTOU race
    // between querying the required size and reading the value.
    std::wstring buffer(32768, L'\0');
    const auto length = GetEnvironmentVariableW(
        variable, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return default_path();
    buffer.resize(length);
    return std::filesystem::path(buffer);
}

void rotate_if_needed(const std::filesystem::path& path) {
    constexpr std::uintmax_t MaxLogBytes = 16U * 1024U * 1024U;
    std::error_code error;
    if (!std::filesystem::exists(path, error) || error ||
        std::filesystem::file_size(path, error) <= MaxLogBytes || error) return;
    auto previous = path;
    previous += L".1";
    std::filesystem::remove(previous, error);
    error.clear();
    std::filesystem::rename(path, previous, error);
    if (error) {
        // rotate_if_needed runs inside ensure_session_open_locked, which is
        // reached with log_mutex already held. Calling write() here would
        // re-acquire the non-recursive mutex on the same thread and deadlock.
        // write_line_locked is the lock-held path; if the file is not open at
        // this point it simply counts the line as dropped without recursing.
        write_line_locked(Level::Warning, "logging",
            std::format("log rotate failed: {}", error.message()));
    }
}

std::string now_text() {
    const auto now = std::chrono::system_clock::now();
    const auto time = std::chrono::system_clock::to_time_t(now);
    std::tm local{};
    localtime_s(&local, &time);
    const auto millis = std::chrono::duration_cast<std::chrono::milliseconds>(
        now.time_since_epoch()).count() % 1000;
    std::ostringstream stream;
    stream << std::put_time(&local, "%Y-%m-%d %H:%M:%S") << '.'
        << std::setfill('0') << std::setw(3) << millis;
    return stream.str();
}

bool ensure_session_open_locked(bool implicit) {
    if (!initialized) {
        initialized = true;
        pending_lines = 0;
        sequence = 0;
        line_count = 0;
        warning_count = 0;
        error_count = 0;
        byte_count = 0;
        session_header_written = false;
        session_id = make_session_id();
        session_started = std::chrono::steady_clock::now();
        last_flush = session_started;
    }

    if (log_file.is_open() && log_file.good()) return true;
    if (log_file.is_open()) {
        log_file.clear();
        log_file.close();
    }
    log_file.clear();

    const auto custom_log_path =
        GetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", nullptr, 0) != 0;
    log_path = configured_path();
    if (!log_path.parent_path().empty())
        std::filesystem::create_directories(log_path.parent_path());
    rotate_if_needed(log_path);
    log_file.open(log_path, std::ios::out | std::ios::app);
    if (!log_file.is_open() || !log_file.good()) {
        if (log_file.is_open()) log_file.close();
        log_file.clear();
        return false;
    }

    const auto dropped_before_open = dropped_write_count;
    if (!session_header_written) {
        log_file << "\n=== iPhoneMirror capture session ===\n";
        log_file << now_text() << " [startup] session=" << session_id
            << " pid=" << GetCurrentProcessId() << " arch="
#if defined(_WIN64)
            << "x64"
#else
            << "x86"
#endif
            << " implicit=" << implicit
            << " log_target=" << (custom_log_path ? "custom" : "default");
        if (dropped_before_open != 0)
            log_file << " dropped_before_start=" << dropped_before_open;
    } else {
        log_file << now_text() << " [log_recovery] session=" << session_id
            << " log_target=" << (custom_log_path ? "custom" : "default")
            << " dropped_before_recovery=" << dropped_before_open;
    }
    log_file << '\n';
    log_file.flush();
    if (!log_file.good()) {
        if (log_file.is_open()) {
            log_file.clear();
            log_file.close();
        }
        log_file.clear();
        return false;
    }

    session_header_written = true;
    dropped_write_count = 0;
    last_flush = std::chrono::steady_clock::now();
    ensure_flush_thread_locked();
    return true;
}

} // namespace

std::string fingerprint(std::string_view value) noexcept {
    try {
        if (value.empty()) return "anon-empty";
        if (value.size() > std::numeric_limits<ULONG>::max()) return "anon-too-large";
        std::call_once(fingerprint_salt_once, initialize_fingerprint_salt);
        if (!fingerprint_salt_ready) return "anon-unavailable";

        BCRYPT_ALG_HANDLE algorithm{};
        BCRYPT_HASH_HANDLE hash{};
        std::array<unsigned char, 32> digest{};
        bool success = BCryptOpenAlgorithmProvider(
            &algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0;
        success = success && BCryptCreateHash(
            algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
        success = success && BCryptHashData(hash, fingerprint_salt.data(),
            static_cast<ULONG>(fingerprint_salt.size()), 0) >= 0;
        success = success && BCryptHashData(hash,
            reinterpret_cast<PUCHAR>(const_cast<char*>(value.data())),
            static_cast<ULONG>(value.size()), 0) >= 0;
        success = success && BCryptFinishHash(hash, digest.data(),
            static_cast<ULONG>(digest.size()), 0) >= 0;
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        if (!success) return "anon-unavailable";

        constexpr char hex[] = "0123456789abcdef";
        std::string result = "anon-";
        result.reserve(result.size() + 24);
        for (std::size_t index = 0; index < 12; ++index) {
            result.push_back(hex[digest[index] >> 4]);
            result.push_back(hex[digest[index] & 0x0f]);
        }
        return result;
    } catch (...) {
        return "anon-unavailable";
    }
}

void initialize() {
    std::scoped_lock lifecycle_lock(lifecycle_mutex);
    std::scoped_lock lock(log_mutex);
    writes_suspended = false;
    try {
        (void)ensure_session_open_locked(false);
    } catch (...) {
        initialized = true;
    }
}

void write(std::string_view message) noexcept {
    try {
        std::scoped_lock lock(log_mutex);
        if (writes_suspended) {
            ++dropped_write_count;
            return;
        }
        if (!ensure_session_open_locked(true)) {
            ++dropped_write_count;
            return;
        }
        write_line_locked(infer_level(message), infer_category(message), message);
    } catch (...) {
        // Diagnostics are best-effort and must never alter product behavior.
    }
}

void write(Level level, std::string_view category, std::string_view message) noexcept {
    try {
        std::scoped_lock lock(log_mutex);
        if (writes_suspended) {
            ++dropped_write_count;
            return;
        }
        if (!ensure_session_open_locked(true)) {
            ++dropped_write_count;
            return;
        }
        write_line_locked(level, category, message);
    } catch (...) {
        // Diagnostics are best-effort and must never alter product behavior.
    }
}

void write_event(Level level, std::string_view category, std::string_view event,
    std::string_view details) noexcept {
    try {
        if (details.empty()) {
            write(level, category, event);
            return;
        }
        write(level, category, std::format("event={} {}", event, details));
    } catch (...) {
        // Formatting failures must not escape from a logging call.
    }
}

bool start_diagnostics(std::string_view id,
    const std::filesystem::path& timeline_path) noexcept {
    try {
        if (id.empty() || id.size() > 40 || !timeline_path.is_absolute() ||
            !std::all_of(id.begin(), id.end(), [](unsigned char character) {
                return (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') || character == '-';
            })) return false;
        std::scoped_lock lock(trace_mutex);
        if (trace_active.load(std::memory_order_acquire) || trace_worker.joinable())
            return false;
        trace_file.open(timeline_path, std::ios::binary | std::ios::trunc);
        if (!trace_file.is_open()) return false;
        trace_session_id.assign(id);
        trace_head = trace_count = 0;
        trace_lost = 0;
        trace_file_lost.store(0, std::memory_order_relaxed);
        trace_writer_failed.store(false, std::memory_order_release);
        trace_rotation_id.store(0, std::memory_order_relaxed);
        trace_orientation_id.store(0, std::memory_order_relaxed);
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        FILETIME now{};
        GetSystemTimePreciseAsFileTime(&now);
        ULARGE_INTEGER utc{};
        utc.LowPart = now.dwLowDateTime;
        utc.HighPart = now.dwHighDateTime;
        enqueue_trace_locked(make_trace_record(DiagnosticEvent::SessionStart,
            0, 0, frequency.QuadPart, static_cast<std::int64_t>(utc.QuadPart),
            0, 0, 0));
        trace_active.store(true, std::memory_order_release);
        trace_worker = std::jthread(run_trace_writer);
        return true;
    } catch (...) {
        trace_active.store(false, std::memory_order_release);
        trace_count = 0;
        trace_file.close();
        return false;
    }
}

bool stop_diagnostics() noexcept {
    try {
        std::jthread worker;
        {
            std::scoped_lock lock(trace_mutex);
            if (!trace_active.exchange(false, std::memory_order_acq_rel)) return false;
            if (trace_count == TraceCapacity) {
                trace_head = (trace_head + 1) % TraceCapacity;
                --trace_count;
                ++trace_lost;
            }
            enqueue_trace_locked(make_trace_record(DiagnosticEvent::SessionStop,
                0, 0, static_cast<std::int64_t>(trace_lost), 0, 0, 0, 0));
            worker = std::move(trace_worker);
        }
        if (worker.joinable()) {
            worker.request_stop();
            trace_ready.notify_one();
            worker.join();
        }
        const auto file_lost = trace_file_lost.load(std::memory_order_acquire);
        if (file_lost != 0)
            write_trace_record(make_trace_record(DiagnosticEvent::DiagnosticOverflow,
                0, 0, static_cast<std::int64_t>(file_lost), 0, 0, 0, 0));
        trace_file.flush();
        const bool success = trace_file.good() &&
            !trace_writer_failed.load(std::memory_order_acquire);
        trace_file.close();
        return success;
    } catch (...) {
        trace_active.store(false, std::memory_order_release);
        return false;
    }
}

bool diagnostics_active() noexcept {
    return trace_active.load(std::memory_order_acquire);
}

void set_diagnostic_context(std::uint64_t rotation_id,
    std::uint64_t orientation_event_id) noexcept {
    trace_rotation_id.store(rotation_id, std::memory_order_relaxed);
    trace_orientation_id.store(orientation_event_id, std::memory_order_relaxed);
}

void diagnostic_event(DiagnosticEvent event, std::uint64_t frame_id,
    std::uint64_t visual_generation, std::int64_t value0, std::int64_t value1,
    std::int64_t value2, std::int64_t value3, std::int32_t hresult) noexcept {
    if (!diagnostics_active()) return;
    try {
        const auto record = make_trace_record(event, frame_id, visual_generation,
            value0, value1, value2, value3, hresult);
        std::scoped_lock lock(trace_mutex);
        if (trace_active.load(std::memory_order_relaxed)) enqueue_trace_locked(record);
    } catch (...) {
        // The ordinary capture and renderer paths must never depend on a trace.
    }
}

void shutdown() {
    if (diagnostics_active()) (void)stop_diagnostics();
    std::scoped_lock lifecycle_lock(lifecycle_mutex);
    std::jthread worker;
    {
        std::scoped_lock lock(log_mutex);
        // Do this before joining the flush worker. Late writes from API calls
        // already in flight must not lazily reopen the file after shutdown.
        // The next explicit initialize call re-enables writes.
        writes_suspended = true;
        if (!initialized) return;
        worker = std::move(flush_thread);
    }
    if (worker.joinable()) {
        worker.request_stop();
        worker.join();
    }
    {
        std::scoped_lock lock(log_mutex);
        if (log_file.is_open()) {
            const auto elapsed = session_started.time_since_epoch().count() == 0
                ? 0.0
                : std::chrono::duration<double>(
                    std::chrono::steady_clock::now() - session_started).count();
            // A previous write or flush failure leaves failbit set. Clear it
            // for one final best-effort summary, then close the actual handle
            // regardless of whether that summary succeeds.
            log_file.clear();
            log_file << now_text() << " [shutdown] session=" << session_id
                << " lines=" << line_count << " warnings=" << warning_count
                << " errors=" << error_count << " bytes=" << byte_count
                << " elapsed_seconds=" << std::fixed << std::setprecision(3)
                << elapsed << " dropped_after_start=" << dropped_write_count << '\n';
            log_file.flush();
            log_file.close();
        }
        log_file.clear();
        initialized = false;
        session_header_written = false;
        pending_lines = 0;
        session_id.clear();
        session_started = {};
    }
}

} // namespace iPhoneMirror::logging
