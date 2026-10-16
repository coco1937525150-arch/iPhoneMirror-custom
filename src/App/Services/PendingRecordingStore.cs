using System.IO;

namespace IPhoneMirror.App.Services;

internal static class PendingRecordingStore
{
    private const string PartialSuffixMp4 = ".partial.mp4";
    private const string PartialSuffixMov = ".partial.mov";

    internal static string DefaultDirectory => Path.Combine(
        Path.GetTempPath(), "iPhoneMirror", "PendingRecordings");

    internal static string CreatePath(bool transparentPhoneShell = false)
    {
        Directory.CreateDirectory(DefaultDirectory);
        var extension = transparentPhoneShell ? ".mov" : ".mp4";
        return Path.Combine(DefaultDirectory,
            $"iPhoneMirror_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension}");
    }

    internal static string CreateStagingPath(string completedPath)
    {
        var directory = Path.GetDirectoryName(completedPath);
        var name = Path.GetFileNameWithoutExtension(completedPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A complete recording path is required.",
                nameof(completedPath));
        var extension = Path.GetExtension(completedPath);
        if (!extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .mov and .mp4 are valid recording formats.",
                nameof(completedPath));
        return Path.Combine(directory, name + ".partial" + extension);
    }

    internal static string? FindLatest(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory)) return null;
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => file.Length > 0 &&
                    (file.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                     file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) &&
                    !file.Name.EndsWith(PartialSuffixMp4, StringComparison.OrdinalIgnoreCase) &&
                    !file.Name.EndsWith(PartialSuffixMov, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            DiagnosticLogger.Exception("recording", "pending_recording_scan_failed",
                error, ("directory", Path.GetFileName(directory)));
            return null;
        }
    }
}
