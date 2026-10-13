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
        if (shellImageFile is { Length: > 0 } && !File.Exists(shellImageFile))
            throw new FileNotFoundException("The chosen phone-shell image no longer exists.", shellImageFile);
        if (width is < 160 or > 4096 || height is < 160 or > 4096)
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
        // The output screen crop must rotate with the shell, not simply
        // reuse the portrait offsets when output is landscape.
        var portraitOuter = landscape ? new Rect(
            outer.X + (outer.Width - outer.Height) / 2,
            outer.Y + (outer.Height - outer.Width) / 2,
            outer.Height, outer.Width) : outer;
        var portraitScreen = profile.GetScreenRect(
            (int)portraitOuter.Width, (int)portraitOuter.Height);
        var inner = landscape
            ? new DeviceScreenRect(
                (int)Math.Round(portraitOuter.Y + portraitScreen.Y - outer.Y),
                (int)Math.Round(portraitOuter.X + portraitScreen.X - outer.X),
                portraitScreen.Height, portraitScreen.Width)
            : portraitScreen;
        var screenX = landscape
            ? (int)Math.Round(outer.X + outer.Width / 2 +
                (portraitOuter.Y + portraitOuter.Height / 2) -
                (portraitOuter.Y + portraitScreen.Y + portraitScreen.Height))
            : (int)outer.X + inner.X;
        var screenY = landscape
            ? (int)Math.Round(outer.Y + outer.Height / 2 +
                (portraitOuter.X + portraitScreen.X) -
                (portraitOuter.X + portraitOuter.Width / 2))
            : (int)outer.Y + inner.Y;
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
                    if (landscape)
                        context.PushTransform(new RotateTransform(90,
                            outer.X + outer.Width / 2, outer.Y + outer.Height / 2));
                    var sourceScreen = new Rect(
                        portraitOuter.X + portraitScreen.X,
                        portraitOuter.Y + portraitScreen.Y,
                        portraitScreen.Width, portraitScreen.Height);
                    if (!string.IsNullOrWhiteSpace(shellImageFile))
                        DrawImportedShell(context, shellImageFile, portraitOuter, sourceScreen);
                    else
                        DrawDefaultShell(context, portraitOuter, sourceScreen);
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

    private static void DrawImportedShell(DrawingContext context, string imageFile, Rect outer, Rect screen)
    {
        // User-provided white-background iPhone 16 Pro Max template. Only the
        // physical shell and buttons survive: white areas remain transparent.
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri(Path.GetFullPath(imageFile), UriKind.Absolute);
        source.EndInit();
        source.Freeze();
        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        var converter = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converter.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luma = (pixels[i] * 11 + pixels[i + 1] * 59 +
                        pixels[i + 2] * 30) / 100;
            // Smooth white removal also avoids a giant opaque white rectangle
            // hiding the incoming iPhone video.
            pixels[i + 3] = (byte)Math.Min(pixels[i + 3],
                Math.Clamp((238 - luma) * 255 / 22, 0, 255));
        }
        var writable = BitmapSource.Create(source.PixelWidth, source.PixelHeight,
            96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var cropX = (int)(source.PixelWidth * .09);
        var cropY = (int)(source.PixelHeight * .02);
        var cropW = (int)(source.PixelWidth * .81);
        var cropH = (int)(source.PixelHeight * .945);
        var cropped = new CroppedBitmap(writable,
            new Int32Rect(cropX, cropY, cropW, cropH));
        // The source can be an opaque photo with a BLACK sample display. Mask
        // out the exact video aperture, otherwise the imported template
        // obscures the actual phone picture in the encoded output.
        var margin = Math.Max(8.0, outer.Width * 0.12);
        var bounds = new RectangleGeometry(new Rect(
            outer.X - margin, outer.Y - margin,
            outer.Width + 2 * margin, outer.Height + 2 * margin));
        var apertureRadius = Math.Min(screen.Width, screen.Height) * 0.07;
        var aperture = new RectangleGeometry(screen, apertureRadius, apertureRadius);
        context.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
            bounds, aperture));
        context.DrawImage(cropped, outer);
        context.Pop();
    }
}
