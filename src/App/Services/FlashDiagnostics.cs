using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

// A single process-wide trace prevents independent preview windows from
// racing to replace the native timeline or exporting a half-written file.
internal static class FlashDiagnostics
{
    private static readonly object Gate = new();
    private static Session? _session;
    private static Task<string>? _exportTask;
    private static long _nextRotationId;
    private static long _nextOrientationId;
    private static readonly Guid DocumentsFolder = new(
        "FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Regex PrivateData = new(
        @"(?i)(?:[A-Z]:\\|\\\\)[^\s""'<>]+|https?://[^\s""'<>]+|\b[0-9a-f]{40}\b|\b[0-9a-f]{8}-[0-9a-f]{16}\b|\b[\w.+-]+@[\w.-]+\.[a-z]{2,}\b|(?<=password[=:])[^\s,;]+|(?<=cookie[=:])[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool Active { get { lock (Gate) return _session is not null; } }

    internal static void Start()
    {
        lock (Gate)
        {
            if (_session is not null) throw new InvalidOperationException("闪屏诊断已经在运行。");
            if (_exportTask is { IsCompleted: false })
                throw new InvalidOperationException("上一次诊断仍在导出。");
            _exportTask = null;
            var directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "iPhoneMirror",
                "Diagnostics", "Working", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var session = new Session(Guid.NewGuid().ToString("N"), directory,
                DateTimeOffset.UtcNow);
            try
            {
                NativeCore.StartFlickerDiagnostics(session.Id,
                    Path.Combine(directory, "timeline.jsonl"));
                _session = session;
                NativeCore.SetFlickerDiagnosticContext(0, 0);
                DiagnosticLogger.Info("flash_diagnostics", "started");
            }
            catch
            {
                Directory.Delete(directory, recursive: true);
                throw;
            }
        }
    }

    internal static ulong NextOrientation(uint width, uint height, bool landscape)
    {
        if (!Active) return 0;
        var id = (ulong)Interlocked.Increment(ref _nextOrientationId);
        NativeCore.SetFlickerDiagnosticContext(0, id);
        NativeCore.TraceFlickerEvent(3, value0: width, value1: height,
            value2: landscape ? 1 : 0);
        return id;
    }

    internal static ulong NextRotation(double fromAngle, double targetAngle,
        int width, int height, ulong orientationId)
    {
        if (!Active) return 0;
        var id = (ulong)Interlocked.Increment(ref _nextRotationId);
        NativeCore.SetFlickerDiagnosticContext(id, orientationId);
        NativeCore.TraceFlickerEvent(5, value0: MilliDegrees(fromAngle),
            value1: MilliDegrees(targetAngle), value2: width, value3: height);
        return id;
    }

    internal static void Trace(uint code, long value0 = 0, long value1 = 0,
        long value2 = 0, long value3 = 0)
    {
        if (Active) NativeCore.TraceFlickerEvent(code,
            value0: value0, value1: value1, value2: value2, value3: value3);
    }

    internal static long MilliDegrees(double angle) =>
        (long)Math.Round(angle * 1000, MidpointRounding.AwayFromZero);

    internal static Task<string> StopAndExportAsync()
    {
        lock (Gate)
        {
            if (_exportTask is not null) return _exportTask;
            if (_session is null) throw new InvalidOperationException("尚未开始闪屏诊断。");
            _exportTask = Task.Run(StopAndExport);
            return _exportTask;
        }
    }

    internal static void WaitForPendingExport()
    {
        Task<string>? task;
        lock (Gate)
            task = _exportTask ?? (_session is not null ? StopAndExportAsync() : null);
        if (task is null) return;
        try { _ = task.GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("flash_diagnostics", "shutdown_export_failed", error);
        }
    }

    private static string StopAndExport()
    {
        Session session;
        lock (Gate)
        {
            session = _session ?? throw new InvalidOperationException("尚未开始闪屏诊断。");
            _session = null;
            NativeCore.SetFlickerDiagnosticContext(0, 0);
        }
        NativeCore.StopFlickerDiagnostics();
        try
        {
            var folder = Path.Combine(session.Directory, "iPhoneMirror-Flash-Diagnostics");
            Directory.CreateDirectory(folder);
            var timeline = Path.Combine(session.Directory, "timeline.jsonl");
            if (!File.Exists(timeline)) throw new FileNotFoundException("时间线日志不存在。", timeline);
            CopyRedactedRequired(DiagnosticLogger.Path, Path.Combine(folder, "application.log"));
            CopyRedactedRequired(DiagnosticLogger.NativeLogPath, Path.Combine(folder, "capture.log"));
            var events = ReadTimeline(timeline);
            events.Sort((a, b) => GetNumber(a, "qpc_ticks").CompareTo(
                GetNumber(b, "qpc_ticks")));
            File.WriteAllLines(Path.Combine(folder, "timeline.jsonl"),
                events.Select(e => e.GetRawText()), Encoding.UTF8);
            var rotations = events.Where(e => e.GetProperty("event").GetString() is
                "rotation_started" or "rotation_progress" or "rotation_retargeted" or
                "rotation_completed" or "rotation_aborted");
            File.WriteAllLines(Path.Combine(folder, "rotation-events.jsonl"),
                rotations.Select(e => e.GetRawText()), Encoding.UTF8);
            var frequency = events.FirstOrDefault(e =>
                e.GetProperty("event").GetString() == "diagnostic_start");
            var qpcFrequency = GetNumber(frequency, "qpc_frequency");
            var durationTicks = events.Count >= 2
                ? GetNumber(events[^1], "qpc_ticks") - GetNumber(events[0], "qpc_ticks") : 0;
            var counts = events.GroupBy(e => e.GetProperty("event").GetString() ?? "unknown")
                .ToDictionary(g => g.Key, g => g.Count());
            int Count(string name) => counts.GetValueOrDefault(name);
            var gaps = events.Where(e => e.GetProperty("event").GetString() == "present_end")
                .Select(e => GetNumber(e, "qpc_ticks")).ToArray();
            var maxPresentGap = gaps.Zip(gaps.Skip(1), (a, b) => b - a)
                .DefaultIfEmpty().Max();
            var ringEventsLost = events.Where(e => e.GetProperty("event").GetString() ==
                "diagnostic_stop").Sum(e => GetNumber(e, "ring_events_lost"));
            var fileEventsLost = events.Where(e => e.GetProperty("event").GetString() ==
                "diagnostic_overflow").Sum(e => GetNumber(e, "events_lost"));
            var rotationsCompleted = events.Where(e => e.GetProperty("event").GetString() ==
                "rotation_completed").Select(e => GetNumber(e, "elapsed_ms")).ToArray();
            var maximumVideoGap = events.Where(e => e.GetProperty("event").GetString() ==
                "frame_gap").Select(e => GetNumber(e, "interval_us"))
                .DefaultIfEmpty().Max();
            var rotationStartDelays = events.Where(e => e.GetProperty("event").GetString() ==
                "rotation_started").Select(start =>
            {
                var orientationId = GetNumber(start, "orientation_event_id");
                var detected = events.FirstOrDefault(e =>
                    e.GetProperty("event").GetString() == "orientation_detected" &&
                    GetNumber(e, "orientation_event_id") == orientationId);
                return orientationId != 0 && detected.ValueKind == JsonValueKind.Object &&
                    qpcFrequency > 0
                    ? (GetNumber(start, "qpc_ticks") - GetNumber(detected, "qpc_ticks")) *
                        1000.0 / qpcFrequency
                    : (double?)null;
            }).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            var animationHwndResizes = events.Count(e =>
                e.GetProperty("event").GetString() == "hwnd_resize" &&
                GetNumber(e, "rotation_id") != 0);
            var stats = new
            {
                session_id = session.Id,
                duration_ms = qpcFrequency > 0 ? durationTicks * 1000.0 / qpcFrequency : 0,
                source_frames_received = Count("source_frame_received"),
                frames_submitted = Count("frame_submitted"),
                presents_succeeded = events.Count(e => e.GetProperty("event").GetString() ==
                    "present_end" && GetNumber(e, "hresult") == 0),
                frames_dropped = Count("frame_dropped") + Count("source_frame_dropped"),
                presents_failed_or_skipped = events.Count(e => e.GetProperty("event").GetString() ==
                    "present_end" && GetNumber(e, "hresult") != 0),
                device_rebuilds = Count("device_rebuild_begin"),
                resize_buffers = Count("resize_begin"),
                hwnd_resizes = Count("hwnd_resize"),
                orientation_events = Count("orientation_detected"),
                rotations_started = Count("rotation_started"),
                rotations_completed = Count("rotation_completed"),
                rotation_start_delay_ms = rotationStartDelays,
                rotation_actual_durations_ms = rotationsCompleted,
                animation_hwnd_resizes = animationHwndResizes,
                ring_events_lost = ringEventsLost,
                file_limit_events_lost = fileEventsLost,
                max_video_frame_interval_ms = maximumVideoGap / 1000.0,
                max_present_interval_ms = qpcFrequency > 0
                    ? maxPresentGap * 1000.0 / qpcFrequency : 0,
                recording_source_stalls = Count("recording_source_stall"),
                recording_repeat_events = Count("recording_frame_repeat"),
                recording_encoder_stalls = Count("recording_encoder_stall"),
                recording_unstable_windows = Count("recording_unstable_window"),
                recording_stability = Count("recording_unstable_window") > 0
                    ? "unstable_detected" : Count("recording_frame_repeat") > 0
                        ? "repeated_frames_observed" : "not_proven_stable",
                diagnostic_fps_delta = "unavailable_without_baseline",
            };
            WriteJson(Path.Combine(folder, "render-statistics.json"), stats);
            WriteJson(Path.Combine(folder, "environment.json"), new
            {
                windows_version = Environment.OSVersion.VersionString,
                app_version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                system_dpi = ReadPrimaryDisplayCapability(88),
                primary_display_refresh_hz = ReadPrimaryDisplayCapability(116),
                installed_display_adapter = ReadDisplayAdapter(),
                rendering_mode = "DirectComposition/D3D11",
            });
            var provenance = Path.Combine(AppContext.BaseDirectory, "BUILD_PROVENANCE.txt");
            File.WriteAllText(Path.Combine(folder, "BUILD_PROVENANCE.txt"),
                File.Exists(provenance) ? File.ReadAllText(provenance) :
                "Build provenance unavailable in this installation.\n", Encoding.UTF8);
            var summary = new StringBuilder();
            summary.AppendLine("iPhoneMirror 闪屏诊断摘要");
            summary.AppendLine($"记录旋转：{Count("rotation_started")} 次；完成：{Count("rotation_completed")} 次。");
            summary.AppendLine($"Present 失败或跳过：{stats.presents_failed_or_skipped} 次；资源重建：{stats.device_rebuilds} 次。");
            summary.AppendLine($"关联到旋转的 HWND 尺寸变化：{animationHwndResizes} 次；是否异常需核对各次旋转的阶段尺寸。");
            summary.AppendLine($"视频与渲染关联：{(Count("source_frame_received") > 0 && Count("frame_submitted") > 0 ? "有帧 ID 数据" : "数据不足")}。");
            summary.AppendLine($"环形缓冲区丢失事件：{ringEventsLost}；文件上限丢失事件：{fileEventsLost}。");
            summary.AppendLine($"录制输入停顿：{stats.recording_source_stalls} 次；编码管道卡顿：{stats.recording_encoder_stalls} 次；不稳定窗口：{stats.recording_unstable_windows} 次。");
            summary.AppendLine($"录制稳定性：{stats.recording_stability}。录制未启动时不能以此推断录制质量。");
            summary.AppendLine("Present 成功不代表该帧已经显示到屏幕。日志不能单独证明可见闪屏。");
            summary.AppendLine("visual_flash_not_confirmed");
            File.WriteAllText(Path.Combine(folder, "diagnostic-summary.txt"), summary.ToString(), Encoding.UTF8);
            var destination = GetExportDirectory();
            var name = $"iPhoneMirror-Flash-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            var finalPath = CreateValidatedZip(folder, destination, name, session.Id);
            Directory.Delete(session.Directory, recursive: true);
            return finalPath;
        }
        catch
        {
            // Preserve staging data for support when export fails.
            throw;
        }
    }

    internal static string CreateValidatedZip(string folder, string destination,
        string name, string sessionId)
    {
        Directory.CreateDirectory(destination);
        var finalPath = Path.Combine(destination, name);
        var pending = finalPath + ".tmp-" + sessionId;
        try
        {
            ZipFile.CreateFromDirectory(folder, pending,
                CompressionLevel.Optimal, includeBaseDirectory: true,
                entryNameEncoding: Encoding.UTF8);
            using (var archive = ZipFile.OpenRead(pending))
            {
                var expected = new[] { "application.log", "capture.log",
                    "rotation-events.jsonl", "timeline.jsonl", "render-statistics.json",
                    "environment.json", "BUILD_PROVENANCE.txt", "diagnostic-summary.txt" };
                foreach (var item in expected)
                {
                    var entry = archive.GetEntry("iPhoneMirror-Flash-Diagnostics/" + item) ??
                        throw new InvalidDataException($"ZIP 缺少 {item}。");
                    if (item.EndsWith(".json", StringComparison.Ordinal))
                    {
                        using var stream = entry.Open();
                        using var document = JsonDocument.Parse(stream);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException($"{item} 不是 JSON 对象。");
                    }
                    else if (item.EndsWith(".jsonl", StringComparison.Ordinal))
                    {
                        using var reader = new StreamReader(entry.Open());
                        while (reader.ReadLine() is { } line)
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                using var document = JsonDocument.Parse(line);
                                if (document.RootElement.ValueKind != JsonValueKind.Object)
                                    throw new InvalidDataException($"{item} 包含无效事件。");
                            }
                    }
                }
            }
            File.Move(pending, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            if (File.Exists(pending)) File.Delete(pending);
            throw;
        }
    }

    private static long GetNumber(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.TryGetInt64(out var number) ? number : 0;

    private static List<JsonElement> ReadTimeline(string path)
    {
        var result = new List<JsonElement>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            result.Add(document.RootElement.Clone());
        }
        return result;
    }

    private static void CopyRedactedRequired(string source, string target)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("必需日志不存在。", source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(input);
        using var writer = new StreamWriter(target, false, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            writer.WriteLine(Redact(line));
    }

    internal static string Redact(string line) => PrivateData.Replace(line, "[redacted]");

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

    private static string GetExportDirectory()
    {
        try
        {
            if (SHGetKnownFolderPath(DocumentsFolder, 0, 0, out var pointer) >= 0)
            {
                try
                {
                    var documents = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrWhiteSpace(documents))
                        return EnsureWritable(Path.Combine(documents, "iPhoneMirror", "Diagnostics"));
                }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return EnsureWritable(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "iPhoneMirror", "Diagnostics"));
    }

    private static string EnsureWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        return directory;
    }

    private static int? ReadPrimaryDisplayCapability(int index)
    {
        var dc = GetDC(0);
        if (dc == 0) return null;
        try
        {
            var value = GetDeviceCaps(dc, index);
            return value > 0 ? value : null;
        }
        finally { _ = ReleaseDC(0, dc); }
    }

    private static object ReadDisplayAdapter()
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in adapters?.GetSubKeyNames() ?? [])
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;
                using var adapter = adapters!.OpenSubKey(name);
                var description = adapter?.GetValue("DriverDesc") as string;
                var version = adapter?.GetValue("DriverVersion") as string;
                if (!string.IsNullOrWhiteSpace(description))
                    return new { name = description, driver_version = version ?? "unavailable" };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warning("flash_diagnostics", "gpu_inventory_unavailable");
        }
        return new { name = "unavailable", driver_version = "unavailable" };
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, nint token,
        out nint path);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);

    private sealed record Session(string Id, string Directory, DateTimeOffset Started);
}
