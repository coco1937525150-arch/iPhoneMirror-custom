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
    /// Reserve the full swept diagonal once, rather than rescaling the
    /// entire phone on each intermediate angle. Dynamic angle-dependent
    /// resizing made the bezel pulse against a still-updating video frame.
    /// A monitor-limited canvas may require a fixed downscale; never let
    /// either axis of the rotated body escape the temporary square.
    /// </summary>
    internal static float ScaleAtAngle(
        int sourceWidth, int sourceHeight, double canvasSide,
        float finalScale, double degrees)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || canvasSide <= 1)
            return Math.Clamp(finalScale, 0.01f, 1f);
        // The entire sweep must use the SAME scale: theta only affects the
        // rigid DComp rotation matrix, not the dimensions of the phone body.
        _ = degrees;
        var diagonal = Math.Sqrt((double)sourceWidth * sourceWidth +
            (double)sourceHeight * sourceHeight);
        var safeScale = (canvasSide - 1.0) / diagonal;
        return (float)Math.Clamp(Math.Min(finalScale, safeScale), 0.01, 1.0);
    }

}
