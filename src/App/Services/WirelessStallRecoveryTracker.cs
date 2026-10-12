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
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan FirstFrameThreshold = TimeSpan.FromSeconds(4);

    private ulong _handle;
    private uint _width;
    private uint _height;
    private long _timestamp;
    private ulong _videoFrames;
    private DateTimeOffset _lastProgressAt;
    private int _recoveryAttempts;
    private bool _initialized;

    internal int RecoveryAttempts => _recoveryAttempts;

    internal WirelessStallRecoveryAction Observe(
        ulong handle, NativeCaptureStatus status, long latestFrameTimestamp,
        DateTimeOffset now)
    {
        if (handle == 0 || status.State != CaptureState.Streaming)
        {
            Reset();
            return WirelessStallRecoveryAction.None;
        }
        // A receiver that reports Streaming with zero frames is precisely the
        // black-window case. Never reset its timer just because the first
        // decoded frame has not arrived yet.
        var hasFrame = status.Width > 0 && status.Height > 0 &&
            status.VideoFrames > 0 && latestFrameTimestamp > 0;

        var dimensionsChanged = _initialized && hasFrame &&
            _width > 0 && _height > 0 &&
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

        var advanced = hasFrame &&
            (latestFrameTimestamp != _timestamp ||
             status.VideoFrames != _videoFrames);
        if (advanced)
        {
            _timestamp = latestFrameTimestamp;
            _videoFrames = status.VideoFrames;
            _width = status.Width;
            _height = status.Height;
            _lastProgressAt = now;
            _recoveryAttempts = 0;
            return WirelessStallRecoveryAction.None;
        }

        // Real-device diagnostics showed a repeating chain of session handles
        // h1..h25 caused by RestartSession on unchanged frame counters.
        // Replacing the native capture session does not revive an iOS sender,
        // even if the session has yet to receive its FIRST frame. Transport
        // liveness must be diagnosed independently from frame cadence.
        // Allow one low-risk redraw per progress window and never restart
        // on this signal alone.
        var awaitingFirstFrame = _videoFrames == 0;
        var threshold = awaitingFirstFrame ? FirstFrameThreshold : StallThreshold;
        if (now - _lastProgressAt < threshold || _recoveryAttempts >= 1)
            return WirelessStallRecoveryAction.None;

        ++_recoveryAttempts;
        return WirelessStallRecoveryAction.RefreshPreview;
    }

    internal void Reset()
    {
        _handle = 0;
        _width = _height = 0;
        _timestamp = 0;
        _videoFrames = 0;
        _lastProgressAt = default;
        _recoveryAttempts = 0;
        _initialized = false;
    }

}
