namespace IPhoneMirror.App.Services;

/// <summary>
/// Re-targetable, monotonic phone-body rotation. The screen and bezel use ONE
/// DirectComposition transform; no AirPlay frame or iOS layout is rotated.
/// Reproduces CSS cubic-bezier(.76,0,.24,1) over 1000ms, without overshoot.
/// </summary>
internal sealed class PhoneRotationTimeline
{
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1000);

    private double _from;
    private double _target;
    private TimeSpan _started;

    internal double TargetDegrees => _target;

    internal void Retarget(double deltaDegrees, TimeSpan now)
    {
        var current = Sample(now);
        _from = current;
        _target += deltaDegrees;
        _started = now;
    }

    internal double Sample(TimeSpan now)
    {
        var t = Math.Clamp((now - _started).TotalMilliseconds /
            Duration.TotalMilliseconds, 0.0, 1.0);
        return _from + (_target - _from) * Ease(t);
    }

    internal bool Finished(TimeSpan now) =>
        now - _started >= Duration;

    private static double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        // Invert CSS cubic Bezier's X using bisection: p1=(.76,0),
        // p2=(.24,1). The Y value supplies the eased rotation progress.
        double lo = 0, hi = 1, parameter = .5;
        for (var i = 0; i < 22; i++)
        {
            parameter = (lo + hi) * .5;
            var inv = 1 - parameter;
            var x = 3 * inv * inv * parameter * .76 +
                3 * inv * parameter * parameter * .24 +
                parameter * parameter * parameter;
            if (x < progress) lo = parameter;
            else hi = parameter;
        }
        var u = 1 - parameter;
        return 3 * u * parameter * parameter +
            parameter * parameter * parameter;
    }
}
