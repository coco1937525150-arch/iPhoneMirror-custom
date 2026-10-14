using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Separates the central display aperture from the *exterior* transparency of
/// the original unmodified iPhone 16 Pro Max PNG. Both areas have alpha zero;
/// using (255 - alpha) without connectivity would display the video outside
/// the handset's rounded corners.
/// </summary>
internal static class PhoneShellApertureMask
{
    internal static BitmapSource FromShell(BitmapSource shell)
    {
        var source = new FormatConvertedBitmap(shell, PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width < 100 || height < 100 || width > 4096 || height > 4096)
            throw new InvalidOperationException("Unexpected phone-shell mask dimensions.");
        var stride = checked(width * 4);
        var image = new byte[checked(stride * height)];
        source.CopyPixels(image, stride, 0);
        var count = checked(width * height);
        var visited = new byte[count];
        var queue = new int[count];
        var first = (height / 2) * width + width / 2;
        if (image[first * 4 + 3] >= 250)
            throw new InvalidOperationException("Bundled phone shell has no transparent central display.");

        var head = 0;
        var tail = 0;
        visited[first] = 1;
        queue[tail++] = first;
        void Enqueue(int index)
        {
            if (visited[index] != 0 || image[index * 4 + 3] >= 250) return;
            visited[index] = 1;
            queue[tail++] = index;
        }

        while (head < tail)
        {
            var index = queue[head++];
            var x = index % width;
            var y = index / width;
            if (x > 0) Enqueue(index - 1);
            if (x + 1 < width) Enqueue(index + 1);
            if (y > 0) Enqueue(index - width);
            if (y + 1 < height) Enqueue(index + width);
        }
        if (tail < count / 3 || tail > count * 9 / 10)
            throw new InvalidOperationException("Unable to isolate the central screen aperture.");

        // Opaque RGB grayscale: FFmpeg converts this to gray for alphamerge.
        // Alpha is 255 throughout, so fully transparent exterior cannot turn
        // into white if FFmpeg changes its PNG decoding pixel format.
        var mask = new byte[image.Length];
        for (var index = 0; index < count; index++)
        {
            var offset = index * 4;
            var coverage = visited[index] == 0 ? (byte)0 :
                (byte)(255 - image[offset + 3]);
            mask[offset] = coverage;
            mask[offset + 1] = coverage;
            mask[offset + 2] = coverage;
            mask[offset + 3] = 255;
        }
        var result = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, mask, stride);
        result.Freeze();
        return result;
    }
}
