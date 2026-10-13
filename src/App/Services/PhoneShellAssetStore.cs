using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IPhoneMirror.App.Services;

/// <summary>
/// A single on-disk phone-shell asset used by the recording compositor,
/// WPF layout editor and the native independent-window renderer.
/// The selected asset lives in LocalAppData so it works with read-only
/// portable/package install directories.
/// </summary>
internal static class PhoneShellAssetStore
{
    internal const string AssetName = "iphone16_pro_max_shell.png";

    internal static string CustomPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "Assets", AssetName);

    internal static bool HasCustomShell => File.Exists(CustomPath);

    internal static string ActivePath => HasCustomShell ? CustomPath :
        Path.Combine(AppContext.BaseDirectory, "Assets", AssetName);

    internal static void Install(string file, string? destinationPath = null)
    {
        if (!File.Exists(file)) throw new FileNotFoundException(
            "手机壳 PNG 文件不存在。", file);
        var input = new BitmapImage();
        input.BeginInit();
        input.CacheOption = BitmapCacheOption.OnLoad;
        input.UriSource = new Uri(Path.GetFullPath(file));
        input.EndInit();
        if (input.PixelWidth is < 300 or > 2048 ||
            input.PixelHeight is < 600 or > 4096 ||
            input.PixelHeight <= input.PixelWidth ||
            input.PixelWidth / (double)input.PixelHeight is < .35 or > .65)
            throw new InvalidDataException(
                "请选择竖屏 iPhone 手机壳 PNG（宽 300–2048、高 600–4096 像素）。");

        // The supplied reference can have a solid black display area rather
        // than an alpha hole. Convert that display to transparency while
        // preserving the metal rails and the existing PNG exterior alpha.
        var converted = new FormatConvertedBitmap(input,
            PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pitch = checked(width * 4);
        var pixels = new byte[checked(pitch * height)];
        converted.CopyPixels(pixels, pitch, 0);

        var refProfile = DeviceFrameProfileResolver.IPhoneImportedShell;
        var hole = refProfile.GetScreenRect(width, height);
        var radius = Math.Min(hole.Width, hole.Height) * .075;
        var x0 = hole.X;
        var y0 = hole.Y;
        var x1 = hole.X + hole.Width;
        var y1 = hole.Y + hole.Height;
        for (var y = Math.Max(0, y0); y < Math.Min(height, y1); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(width, x1); x++)
            {
                // Signed distance to the rounded inner rectangle. Make
                // antialiasing subpixel-smooth instead of punching a sharp
                // square into the bezel.
                var dx = Math.Max(Math.Max(x0 + radius - x, 0),
                    x - (x1 - radius));
                var dy = Math.Max(Math.Max(y0 + radius - y, 0),
                    y - (y1 - radius));
                var distance = Math.Sqrt(dx * dx + dy * dy) - radius;
                var opacity = Math.Clamp(distance + .5, 0, 1);
                var offset = y * pitch + x * 4 + 3;
                pixels[offset] = (byte)Math.Round(pixels[offset] * opacity);
            }
        }

        var corrected = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, pitch);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(corrected));
        var destination = destinationPath ?? CustomPath;
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(staging)) png.Save(output);
            File.Move(staging, destination, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
