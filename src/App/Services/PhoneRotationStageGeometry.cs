namespace IPhoneMirror.App.Services;

/// <summary>
/// Geometry for a temporary DirectComposition rotation stage. Every HWND
/// rectangle is anchored at the ORIGINAL phone center, never at the center
/// of a work-area-clamped square. The transformed screen, metal shell and
/// aperture must share exactly the same pivot.
/// </summary>
internal static class PhoneRotationStageGeometry
{
    internal static (int X, int Y) CenteredBounds(
        int sourceLeft, int sourceTop, int sourceRight, int sourceBottom,
        int targetWidth, int targetHeight)
    {
        var centerX = (sourceLeft + (long)sourceRight) * 0.5;
        var centerY = (sourceTop + (long)sourceBottom) * 0.5;
        return (
            checked((int)Math.Round(centerX - targetWidth * 0.5,
                MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(centerY - targetHeight * 0.5,
                MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// Fit the phone's swept bounding box inside the fixed animation square.
    /// Scale decreases smoothly as the device turns, rather than instantly
    /// jumping from 1.0 to the final fitted size when animation begins.
    /// Track the maximum rotated extent ALREADY encountered so zoom never
    /// pulsates as the horizontal/vertical bounds trade places.
    /// </summary>
    internal static float ScaleAtAngle(
        int sourceWidth, int sourceHeight, double canvasSide,
        float finalScale, double degrees)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || canvasSide <= 1)
            return Math.Clamp(finalScale, 0.01f, 1f);
        var theta = Math.Clamp(Math.Abs(degrees), 0, 90) * Math.PI / 180;
        var diagonal = Math.Sqrt((double)sourceWidth * sourceWidth +
            (double)sourceHeight * sourceHeight);
        var horizontalPeak = Math.Atan2(sourceHeight, sourceWidth);
        var verticalPeak = Math.Atan2(sourceWidth, sourceHeight);
        double HorizontalExtent(double angle) =>
            sourceWidth * Math.Cos(angle) + sourceHeight * Math.Sin(angle);
        double VerticalExtent(double angle) =>
            sourceWidth * Math.Sin(angle) + sourceHeight * Math.Cos(angle);

        var widestSeen = theta >= horizontalPeak
            ? diagonal : HorizontalExtent(theta);
        var tallestSeen = theta >= verticalPeak
            ? diagonal : VerticalExtent(theta);
        var maximumSeen = Math.Max(
            Math.Max(sourceWidth, sourceHeight),
            Math.Max(widestSeen, tallestSeen));
        var safeScale = (canvasSide - 1.0) / maximumSeen;
        var easing = Math.Min(1.0, Math.Abs(degrees) / 90.0);
        var progressScale = 1.0 - (1.0 - finalScale) * easing;
        return (float)Math.Clamp(Math.Min(progressScale, safeScale),
            0.01, 1.0);
    }
}
