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
        scale = Math.Clamp(double.IsFinite(scale) ? scale : 1, .1, maxScale);
        var shellWidth = baseWidth * scale;
        var shellHeight = baseHeight * scale;
        centerX = double.IsFinite(centerX) ? centerX : .5;
        centerY = double.IsFinite(centerY) ? centerY : .5;
        var minCenterX = (margin + shellWidth / 2) / canvasWidth;
        var maxCenterX = (canvasWidth - margin - shellWidth / 2) / canvasWidth;
        var minCenterY = (margin + shellHeight / 2) / canvasHeight;
        var maxCenterY = (canvasHeight - margin - shellHeight / 2) / canvasHeight;
        centerX = Math.Clamp(centerX, minCenterX, maxCenterX);
        centerY = Math.Clamp(centerY, minCenterY, maxCenterY);
        return (new Rect(
            Math.Round(canvasWidth * centerX - shellWidth / 2),
            Math.Round(canvasHeight * centerY - shellHeight / 2),
            Math.Round(shellWidth), Math.Round(shellHeight)),
            centerX, centerY, scale);
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
        // The FFmpeg source retains its capture dimensions; the composited
        // canvas is instead the *full* background image at native pixel size.
        var canvasWidth = (int)width;
        var canvasHeight = (int)height;
        if (!string.IsNullOrWhiteSpace(backgroundFile))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(backgroundFile));
            image.EndInit();
            canvasWidth = image.PixelWidth;
            canvasHeight = image.PixelHeight;
        }
        if (canvasWidth is < 160 or > 4096 || canvasHeight is < 160 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(backgroundFile),
                "背景图原始尺寸需要在 160–4096 像素之间。");
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
        var screenX = (int)outer.X + inner.X;
        var screenY = (int)outer.Y + inner.Y;
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
                inner.Width, inner.Height, canvasWidth, canvasHeight);
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
