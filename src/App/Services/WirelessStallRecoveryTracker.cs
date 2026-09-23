using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

internal enum WirelessStallRecoveryAction
{
    None,
    RefreshPreview,
    RestartSession,
}

/// <summary>
/// Detects an AirPlay stream whose decoded-frame identity stopped advancing.
/// FPS and latency are diagnostic telemetry, not proof of a new frame; using
/// them here would let a stalled decoder evade recovery. The tracker is
/// deliberately independent of WPF so the timing and retry policy can be
/// tested without a live receiver.
/// </summary>
internal sealed class WirelessStallRecoveryTracker
{
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(1800);
    internal static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(5);

    private ulong _handle;
    private uint _width;
    private uint _height;
    private long _timestamp;
    private ulong _videoFrames;
    private DateTimeOffset _lastProgressAt;
    private DateTimeOffset _lastActionAt;
    private int _recoveryAttempts;
    private bool _initialized;

    internal int RecoveryAttempts => _recoveryAttempts;

    internal WirelessStallRecoveryAction Observe(
        ulong handle, NativeCaptureStatus status, long latestFrameTimestamp,
        DateTimeOffset now)
    {
        if (handle == 0 || status.State != CaptureState.Streaming ||
            status.Width == 0 || status.Height == 0 || status.VideoFrames == 0 ||
            latestFrameTimestamp <= 0)
        {
            Reset();
            return WirelessStallRecoveryAction.None;
        }

        var dimensionsChanged = _initialized &&
            (_width != status.Width || _height != status.Height);
        var handleChanged = !_initialized || _handle != handle;
        if (handleChanged || dimensionsChanged)
        {
            _handle = handle;
            _width = status.Width;
            _height = status.Height;
            _timestamp = latestFrameTimestamp;
            _videoFrames = status.VideoFrames;
            _lastProgressAt = now;
            if (handleChanged || dimensionsChanged) _recoveryAttempts = 0;
            _initialized = true;
            return WirelessStallRecoveryAction.None;
        }

        var advanced = latestFrameTimestamp != _timestamp ||
            status.VideoFrames != _videoFrames;
        if (advanced)
        {
            _timestamp = latestFrameTimestamp;
            _videoFrames = status.VideoFrames;
            _lastProgressAt = now;
            return WirelessStallRecoveryAction.None;
        }

        if (now - _lastProgressAt < StallThreshold || _recoveryAttempts >= 2)
            return WirelessStallRecoveryAction.None;
        if (_recoveryAttempts == 1 && now - _lastActionAt < RestartCooldown)
            return WirelessStallRecoveryAction.None;

        _recoveryAttempts++;
        _lastActionAt = now;
        return _recoveryAttempts == 1
            ? WirelessStallRecoveryAction.RefreshPreview
            : WirelessStallRecoveryAction.RestartSession;
    }

    internal void Reset()
    {
        _handle = 0;
        _width = _height = 0;
        _timestamp = 0;
        _videoFrames = 0;
        _lastProgressAt = default;
        _lastActionAt = default;
        _recoveryAttempts = 0;
        _initialized = false;
    }

}
