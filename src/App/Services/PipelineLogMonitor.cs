using System.Globalization;
using System.IO;
using System.Text;

namespace IPhoneMirror.App.Services;

/// <summary>
/// A bounded, read-only view of native pipeline telemetry. Never blocks the
/// capture or renderer thread; samples only the tail of the shared native log.
/// </summary>
internal static class PipelineLogMonitor
{
    private const int TailBytes = 256 * 1024;
    private const string Marker = "d3d_preview pipeline_health ";

    internal static string ReadRecent()
    {
        try
        {
            if (!File.Exists(DiagnosticLogger.NativeLogPath))
                return "尚无原生采集日志。";
            using var stream = new FileStream(DiagnosticLogger.NativeLogPath,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0L, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            if (start > 0) reader.ReadLine(); // Discard an incomplete first line.
            var latestByWindow = new Dictionary<string, (DateTime When, string Info)>(
                StringComparer.Ordinal);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var index = line.IndexOf(Marker, StringComparison.Ordinal);
                if (index < 0 || line.Length < 23) continue;
                if (!DateTime.TryParseExact(line[..23],
                    "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var when)) continue;
                var text = line[(index + Marker.Length)..];
                var hwndIndex = text.IndexOf("hwnd=", StringComparison.Ordinal);
                if (hwndIndex < 0) continue;
                var hwndEnd = text.IndexOf(' ', hwndIndex);
                var key = hwndEnd < 0 ? text[hwndIndex..] : text[hwndIndex..hwndEnd];
                latestByWindow[key] = (when, text);
            }
            if (latestByWindow.Count == 0)
                return "尚无实时渲染统计。启动投屏后等待约 1 秒；旧版构建不包含该采样。";
            var now = DateTime.Now;
            var snapshots = latestByWindow.Values
                .OrderByDescending(entry => entry.When)
                .Take(3)
                .ToArray();
            var result = new StringBuilder();
            foreach (var entry in snapshots)
            {
                var age = Math.Max(0, (now - entry.When).TotalSeconds);
                result.Append(age > 5 ? "[数据已过期] " : "[实时] ");
                result.Append(entry.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                result.Append("  ");
                result.AppendLine(entry.Info);
            }
            result.AppendLine("input_fps：渲染线程看到的新帧，不等于手机编码输出 FPS。");
            result.AppendLine("present_fps：成功提交速率，不等于 DWM 实际显示 FPS。");
            result.AppendLine("input_age_ms / present_age_ms：距最近一次新帧或提交的时间；静态画面也可能无新帧。");
            return result.ToString().TrimEnd();
        }
        catch (IOException error)
        {
            return "读取原生日志失败：" + error.Message;
        }
        catch (UnauthorizedAccessException error)
        {
            return "无权读取原生日志：" + error.Message;
        }
    }
}
