namespace IPhoneMirror.App.Models;

internal enum DeviceCutoutKind
{
    None = 0,
    Notch = 1,
    DynamicIsland = 2,
}

/// <summary>
/// Visual-only hardware shell geometry for detached mirroring windows.
/// Insets are expressed as fractions of the source display short edge so the
/// same profile scales consistently across native resolutions and DPI values.
/// </summary>
internal readonly record struct DeviceFrameProfile(
    string Id,
    bool Enabled,
    double SideInsetRatio,
    double TopInsetRatio,
    double BottomInsetRatio,
    double OuterCornerRatio,
    DeviceCutoutKind CutoutKind,
    double CutoutWidthRatio,
    double CutoutHeightRatio,
    double CutoutTopRatio)
{
    internal static readonly DeviceFrameProfile None = new(
        "none", false, 0, 0, 0, 0, DeviceCutoutKind.None, 0, 0, 0);

    internal (uint Width, uint Height) GetOuterDimensions(uint screenWidth, uint screenHeight)
    {
        if (!Enabled || screenWidth == 0 || screenHeight == 0)
            return (screenWidth, screenHeight);

        var shortEdge = Math.Min(screenWidth, screenHeight);
        var horizontal = shortEdge * Math.Max(0, SideInsetRatio) * 2.0;
        var vertical = shortEdge * (Math.Max(0, TopInsetRatio) + Math.Max(0, BottomInsetRatio));
        return (
            Math.Max(1, (uint)Math.Round(screenWidth + horizontal)),
            Math.Max(1, (uint)Math.Round(screenHeight + vertical)));
    }

    internal DeviceScreenRect GetScreenRect(int clientWidth, int clientHeight)
    {
        if (!Enabled || clientWidth <= 0 || clientHeight <= 0)
            return new DeviceScreenRect(0, 0, Math.Max(1, clientWidth), Math.Max(1, clientHeight));

        // Insets are defined relative to the *display* short edge, not the
        // already-expanded outer-window short edge. Invert the expansion used
        // by GetOuterDimensions so the inner rectangle keeps the exact source
        // aspect ratio after arbitrary user resizing.
        var sideRatio = Math.Clamp(SideInsetRatio, 0, 0.30);
        var topRatio = Math.Clamp(TopInsetRatio, 0, 0.35);
        var bottomRatio = Math.Clamp(BottomInsetRatio, 0, 0.35);
        double displayShortEdge;
        if (clientWidth <= clientHeight)
            displayShortEdge = clientWidth / Math.Max(0.01, 1.0 + sideRatio * 2.0);
        else
            displayShortEdge = clientHeight / Math.Max(0.01, 1.0 + topRatio + bottomRatio);

        var left = (int)Math.Round(displayShortEdge * sideRatio);
        var right = left;
        var top = (int)Math.Round(displayShortEdge * topRatio);
        var bottom = (int)Math.Round(displayShortEdge * bottomRatio);
        var width = Math.Max(1, clientWidth - left - right);
        var height = Math.Max(1, clientHeight - top - bottom);
        return new DeviceScreenRect(left, top, width, height);
    }
}

internal readonly record struct DeviceScreenRect(int X, int Y, int Width, int Height)
{
    internal bool Contains(int x, int y) =>
        x >= X && y >= Y && x < X + Width && y < Y + Height;
}
