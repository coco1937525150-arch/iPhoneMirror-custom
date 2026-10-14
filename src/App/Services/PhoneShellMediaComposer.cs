using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Models;
namespace IPhoneMirror.App.Services;

/// <summary>
/// Produces one transparent, correctly scaled phone-shell overlay for FFmpeg.
/// Never captures the desktop: the output contains only the native video,
/// its decorative shell and a caller-selected background.
/// </summary>
internal sealed record PhoneShellComposition(
    string ShellFile, string? BackgroundFile, int ScreenX, int ScreenY,
    int ScreenWidth, int ScreenHeight, int OutputWidth, int OutputHeight)
{
    internal void Cleanup()
    {
        try { File.Delete(ShellFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class PhoneShellLayoutSettingsProvider
{
    internal static Func<PhoneShellLayoutSettings?>? Current;
}

internal sealed record PhoneShellLayoutSettings(double CenterX, double CenterY, double Scale);

internal static class PhoneShellMediaComposer
{
    // Shared by the WPF layout editor and the encoder. Enforce a 5px
    // safety margin on the ORIGINAL background image in both paths.
    internal static (Rect Bounds, double CenterX, double CenterY, double Scale)
        FitInsideCanvas(double canvasWidth, double canvasHeight, double aspect,
            double centerX, double centerY, double scale)
    {
        if (!double.IsFinite(canvasWidth) || !double.IsFinite(canvasHeight) ||
            canvasWidth <= 10 || canvasHeight <= 10 ||
            !double.IsFinite(aspect) || aspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        const double margin = 5;
        var baseHeight = Math.Min(canvasHeight * .94,
            canvasWidth * .94 / aspect);
        var baseWidth = baseHeight * aspect;
        var maxScale = Math.Min(4, Math.Min(
            (canvasWidth - 2 * margin) / baseWidth,
            (canvasHeight - 2 * margin) / baseHeight));
        if (maxScale < .1)
            throw new InvalidOperationException(
                "当前背景画布无法容纳手机壳，请选择较大的背景图片。");
        scale = Math.Clamp(double.IsFinite(scale) ? scale : 1, .1, maxScale);
        // Round the dimensions BEFORE clamping origin. Rounding X and width
        // separately after floating-point bounds checking could put the right
        // or bottom edge one pixel outside the required 5px safe region.
        var shellWidth = Math.Min(Math.Round(baseWidth * scale),
            canvasWidth - 2 * margin);
        var shellHeight = Math.Min(Math.Round(baseHeight * scale),
            canvasHeight - 2 * margin);
        centerX = double.IsFinite(centerX) ? centerX : .5;
        centerY = double.IsFinite(centerY) ? centerY : .5;
        var left = Math.Clamp(Math.Round(canvasWidth * centerX - shellWidth / 2),
            margin, canvasWidth - margin - shellWidth);
        var top = Math.Clamp(Math.Round(canvasHeight * centerY - shellHeight / 2),
            margin, canvasHeight - margin - shellHeight);
        centerX = (left + shellWidth / 2) / canvasWidth;
        centerY = (top + shellHeight / 2) / canvasHeight;
        return (new Rect(left, top, shellWidth, shellHeight),
            centerX, centerY, scale);
    }

    // A small overscan keeps the encoded iOS frame behind the antialiased
    // bezel after WPF layout rounding and FFmpeg chroma alignment. The shell
    // PNG masks the surplus pixels, so the background cannot show as a hairline
    // at the top, bottom, or sides of the screen opening.
    internal static DeviceScreenRect ExpandVideoUnderBezel(
        DeviceScreenRect visibleScreen)
    {
        const int bleed = 3;
        return new DeviceScreenRect(
            visibleScreen.X - bleed, visibleScreen.Y - bleed,
            (visibleScreen.Width + bleed * 2 + 1) & ~1,
            (visibleScreen.Height + bleed * 2 + 1) & ~1);
    }

    internal static PhoneShellComposition Prepare(uint width, uint height,
        string? backgroundFile, string? shellImageFile)
    {
        if (backgroundFile is { Length: > 0 } && !File.Exists(backgroundFile))
            throw new FileNotFoundException("The chosen recording background no longer exists.", backgroundFile);
        // Use the bundled, transparent iPhone 16 Pro Max reference shell.
        // The old user-selected shell path is intentionally ignored.
        var bundledShell = PhoneShellAssetStore.ActivePath;
        if (!File.Exists(bundledShell))
            throw new FileNotFoundException("The built-in phone-shell image is missing.",
                bundledShell);
        // The encoder canvas is independent of the background image pixel size.
        var canvasWidth = (int)width;
        var canvasHeight = (int)height;
        if (canvasWidth is < 160 or > 4096 || canvasHeight is < 160 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(backgroundFile),
                "录制画布尺寸需要在 160–4096 像素之间。");
        if ((canvasWidth & 1) != 0 || (canvasHeight & 1) != 0)
            throw new InvalidOperationException(
                "H.264 NV12 视频要求画布宽高为偶数。请将背景图调整为偶数像素尺寸后重试。");
        // Match the already tuned iPhone 16 Pro Max viewport geometry.
        var profile = DeviceFrameProfileResolver.ActiveIPhoneShell;
        var reference = profile.GetOuterDimensions(1320, 2868);
        var landscape = width > height;
        var aspect = landscape ? reference.Height / (double)reference.Width
            : reference.Width / (double)reference.Height;
        var settings = PhoneShellLayoutSettingsProvider.Current?.Invoke();
        var layoutScale = settings?.Scale ?? 1.0;
        if (!double.IsFinite(layoutScale)) layoutScale = 1;
        layoutScale = Math.Clamp(layoutScale, .1, 4.0);
        var centerX = settings?.CenterX ?? .5;
        var centerY = settings?.CenterY ?? .5;
        if (!double.IsFinite(centerX)) centerX = .5;
        if (!double.IsFinite(centerY)) centerY = .5;
        var outer = FitInsideCanvas(canvasWidth, canvasHeight, aspect,
            centerX, centerY, layoutScale).Bounds;
        var inner = profile.GetScreenRect((int)outer.Width, (int)outer.Height);
        // An exact rectangle can expose a 1–2 pixel background seam under
        // the antialiased shell edge. Expand only the VIDEO; keep the PNG
        // shell and background at their unchanged original canvas positions.
        var coveredScreen = ExpandVideoUnderBezel(inner);
        var screenX = (int)outer.X + coveredScreen.X;
        var screenY = (int)outer.Y + coveredScreen.Y;
        var filename = Path.Combine(Path.GetTempPath(),
            "iphoneMirror-shell-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            void Render()
            {
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    // Render the complete shell as one rotated unit. The
                    // video screen rectangle uses the same profile rotation.
                    var portraitOuter = outer;
                    if (landscape)
                    {
                        context.PushTransform(new RotateTransform(90,
                            outer.X + outer.Width / 2, outer.Y + outer.Height / 2));
                        portraitOuter = new Rect(
                            outer.X + (outer.Width - outer.Height) / 2,
                            outer.Y + (outer.Height - outer.Width) / 2,
                            outer.Height, outer.Width);
                    }
                    DrawImportedShell(context, bundledShell, portraitOuter);
                    if (landscape) context.Pop();
                }
                var bitmap = new RenderTargetBitmap(canvasWidth, canvasHeight,
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                using var output = File.Create(filename);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }
            var dispatcher = Application.Current?.Dispatcher ??
                throw new InvalidOperationException("The phone-shell output requires the WPF application.");
            if (dispatcher.CheckAccess()) Render();
            else dispatcher.Invoke(Render);
            return new(filename, backgroundFile, screenX, screenY,
                coveredScreen.Width, coveredScreen.Height,
                canvasWidth, canvasHeight);
        }
        catch
        {
            try { File.Delete(filename); } catch (IOException) { }
            throw;
        }
    }

    private static void DrawDefaultShell(DrawingContext context, Rect outer, Rect inner)
    {
        var shortSide = Math.Min(outer.Width, outer.Height);
        var outerCorner = shortSide * .165;
        var innerCorner = shortSide * .14;
        var body = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(outer, outerCorner, outerCorner),
            new RectangleGeometry(inner, innerCorner, innerCorner));
        context.DrawGeometry(new LinearGradientBrush(
            Color.FromRgb(39, 44, 47), Color.FromRgb(3, 5, 8), 0),
            new Pen(new SolidColorBrush(Color.FromRgb(21, 23, 25)), 1.5), body);
        var highlight = new Pen(new SolidColorBrush(Color.FromArgb(195, 124, 131, 135)),
            Math.Max(1, shortSide * .003));
        context.DrawRoundedRectangle(null, highlight,
            new Rect(outer.X + 1.5, outer.Y + 1.5, outer.Width - 3, outer.Height - 3),
            outerCorner, outerCorner);
        // Physical button geometry follows the shell and is never interactive.
        var metallic = new LinearGradientBrush(Color.FromRgb(24, 26, 28),
            Color.FromRgb(131, 137, 138), 0);
        var btnW = Math.Max(2, shortSide * .009);
        var btnH = Math.Max(7, shortSide * .09);
        var xLeft = outer.X - btnW * .62;
        var radius = btnW * .5;
        foreach (var position in new[] { .20, .30, .39 })
            context.DrawRoundedRectangle(metallic, highlight,
                new Rect(xLeft, outer.Y + outer.Height * position, btnW, btnH),
                radius, radius);
        context.DrawRoundedRectangle(metallic, highlight,
            new Rect(outer.Right - btnW * .38, outer.Y + outer.Height * .30,
                btnW, btnH * 1.7), radius, radius);
    }

    private static void DrawImportedShell(DrawingContext context, string imageFile, Rect outer)
    {
        // The embedded PNG is already alpha-masked and tightly cropped.
        // Do not white-key or crop it again: doing so removes side buttons
        // and shifts the bezel relative to the live video.
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri(Path.GetFullPath(imageFile), UriKind.Absolute);
        source.EndInit();
        source.Freeze();
        context.DrawImage(source, outer);
    }
}
