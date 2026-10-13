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
    int ScreenWidth, int ScreenHeight)
{
    internal void Cleanup()
    {
        try { File.Delete(ShellFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class PhoneShellMediaComposer
{
    internal static PhoneShellComposition Prepare(uint width, uint height,
        string? backgroundFile, string? shellImageFile)
    {
        if (backgroundFile is { Length: > 0 } && !File.Exists(backgroundFile))
            throw new FileNotFoundException("The chosen recording background no longer exists.", backgroundFile);
        // Use the bundled, transparent iPhone 16 Pro Max reference shell.
        // The old user-selected shell path is intentionally ignored.
        var bundledShell = Path.Combine(AppContext.BaseDirectory,
            "Assets", "iphone16_pro_max_shell.png");
        if (!File.Exists(bundledShell))
            throw new FileNotFoundException("The built-in phone-shell image is missing.",
                bundledShell);
        if (width is < 160 or > 3840 || height is < 160 or > 2160)
            throw new ArgumentOutOfRangeException(nameof(width));
        // Match the already tuned iPhone 16 Pro Max viewport geometry.
        var profile = DeviceFrameProfileResolver.IPhoneDynamicIsland;
        var reference = profile.GetOuterDimensions(1320, 2868);
        var landscape = width > height;
        var aspect = landscape ? reference.Height / (double)reference.Width
            : reference.Width / (double)reference.Height;
        var outerHeight = Math.Min(height * .94, width * .94 / aspect);
        var outerWidth = outerHeight * aspect;
        var outer = new Rect(
            Math.Round((width - outerWidth) / 2), Math.Round((height - outerHeight) / 2),
            Math.Round(outerWidth), Math.Round(outerHeight));
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
                var bitmap = new RenderTargetBitmap((int)width, (int)height,
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
                inner.Width, inner.Height);
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
