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

    /// <summary>
    /// Remove ONLY a recording's own stale .partial.mov/.partial.mp4 and
    /// the app-owned pending folder if it is empty. Never glob/delete user
    /// completed MOV files, and never touch an unrelated target directory.
    /// Saving or discarding a completed video may call this safely.
    /// </summary>
    internal static void CleanupOwnTemporaryArtifacts(string completedPath,
        string? ownedDirectory = null)
    {
        ownedDirectory ??= DefaultDirectory;
        var directory = Path.GetFullPath(ownedDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(Path.GetFullPath(completedPath));
        if (!string.Equals(directory, parent?.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return;
        var filename = Path.GetFileName(completedPath);
        if (!filename.StartsWith("iPhoneMirror_", StringComparison.OrdinalIgnoreCase) ||
            !(filename.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
              filename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
            return;
        var partial = CreateStagingPath(completedPath);
        if (File.Exists(partial))
        {
            try { File.Delete(partial); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                DiagnosticLogger.Exception("recording", "partial_cleanup_failed", error,
                    ("file", Path.GetFileName(partial)));
            }
        }
        // An empty app-owned temporary folder no longer needs to exist.
        // Directory.Delete(nonrecursive) deliberately refuses to remove a
        // folder with any other pending video or unrelated user file.
        try
        {
            if (Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warning("recording", "empty_pending_folder_cleanup_skipped",
                ("reason", error.GetType().Name));
        }
    }

    /// <summary>
    /// Best-effort deletion of ONLY this completed recording's own
    /// destination-side intermediates. Never scan or delete arbitrary MOVs,
    /// other recording basenames, or the user-selected output directory.
    /// Called after File.Move succeeds (or a complete cross-volume copy is
    /// atomically renamed into the final destination).
    /// </summary>
    internal static int CleanupSavedRecordingSidecars(string savedPath)
    {
        if (string.IsNullOrWhiteSpace(savedPath)) return 0;
        var completed = Path.GetFullPath(savedPath);
        var directory = Path.GetDirectoryName(completed);
        var fileName = Path.GetFileName(completed);
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(directory) ||
            !fileName.StartsWith("iPhoneMirror_", StringComparison.OrdinalIgnoreCase) ||
            !(extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
              extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) ||
            !File.Exists(completed))
            return 0;

        // FFmpeg and our cross-volume save use precisely these intermediate
        // names. Copy sidecars also carry a 32-digit GUID to disambiguate.
        var partialName = Path.GetFileNameWithoutExtension(fileName) +
            ".partial" + extension;
        var copyPrefix = "." + fileName + ".partial-";
        var removed = 0;
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(directory,
                         "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(candidate);
                var isOwnStaging = name.Equals(partialName,
                    StringComparison.OrdinalIgnoreCase);
                var isOwnCrossVolumeCopy = name.StartsWith(copyPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParseExact(name[copyPrefix.Length..], "N", out _);
                if (!isOwnStaging && !isOwnCrossVolumeCopy) continue;
                try
                {
                    // Do not traverse or delete reparse-point targets.
                    if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    File.Delete(candidate);
                    removed++;
                }
                catch (Exception error) when (error is IOException or
                                               UnauthorizedAccessException)
                {
                    DiagnosticLogger.Warning("recording",
                        "destination_sidecar_cleanup_failed",
                        ("file", name), ("reason", error.GetType().Name));
                }
            }
        }
        catch (Exception error) when (error is IOException or
                                       UnauthorizedAccessException)
        {
            DiagnosticLogger.Warning("recording",
                "destination_sidecar_scan_failed",
                ("reason", error.GetType().Name));
        }
        return removed;
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
