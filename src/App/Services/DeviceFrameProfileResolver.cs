using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Resolves a lightweight device-body shell and display cutout from Apple's
/// ProductType. These values are visual fits rather than Apple-published CAD
/// dimensions; keeping them separate from display-corner profiles lets the
/// shell evolve without changing capture or decoder behavior.
/// </summary>
internal static class DeviceFrameProfileResolver
{
    internal static readonly DeviceFrameProfile IPhoneHomeButton = new(
        "iphone-home-button", true,
        0.045, 0.145, 0.175, 0.185,
        DeviceCutoutKind.None, 0, 0, 0);

    internal static readonly DeviceFrameProfile IPhoneNotch = new(
        "iphone-notch", true,
        0.036, 0.040, 0.040, 0.205,
        DeviceCutoutKind.Notch, 0.535, 0.105, 0.000);

    internal static readonly DeviceFrameProfile IPhoneDynamicIsland = new(
        "iphone-dynamic-island", true,
        0.034, 0.038, 0.038, 0.205,
        DeviceCutoutKind.DynamicIsland, 0.315, 0.092, 0.030);

    internal static readonly DeviceFrameProfile IPad = new(
        "ipad", true,
        0.026, 0.026, 0.026, 0.060,
        DeviceCutoutKind.None, 0, 0, 0);

    private static readonly HashSet<(int Major, int Minor)> HomeButtonIPhones =
    [
        (10, 1), (10, 2), (10, 4), (10, 5),
        (12, 8), (14, 6),
    ];

    private static readonly HashSet<(int Major, int Minor)> NotchedModernIPhones =
    [
        (17, 5), // iPhone 16e
    ];

    internal static DeviceFrameProfile Resolve(
        string? productType,
        uint frameWidth = 0,
        uint frameHeight = 0)
    {
        if (TryParseProductType(productType, "iPhone", out var major, out var minor))
        {
            if (major < 10 || HomeButtonIPhones.Contains((major, minor)))
                return IPhoneHomeButton;
            if (major == 10)
                return minor is 3 or 6 ? IPhoneNotch : IPhoneHomeButton;
            if (NotchedModernIPhones.Contains((major, minor))) return IPhoneNotch;
            return major >= 15 ? IPhoneDynamicIsland : IPhoneNotch;
        }

        if (TryParseProductType(productType, "iPad", out _, out _)) return IPad;
        if (!string.IsNullOrWhiteSpace(productType)) return DeviceFrameProfile.None;

        if (frameWidth == 0 || frameHeight == 0) return DeviceFrameProfile.None;
        var shortEdge = Math.Min(frameWidth, frameHeight);
        var longEdge = Math.Max(frameWidth, frameHeight);
        var ratio = shortEdge / (double)longEdge;
        if (ratio is >= 0.38 and <= 0.56) return IPhoneDynamicIsland;
        if (ratio is >= 0.64 and <= 0.80) return IPad;
        return DeviceFrameProfile.None;
    }

    private static bool TryParseProductType(
        string? value,
        string prefix,
        out int major,
        out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = value.AsSpan(prefix.Length);
        var comma = suffix.IndexOf(',');
        if (comma <= 0 || comma == suffix.Length - 1) return false;
        return int.TryParse(suffix[..comma], out major) &&
            int.TryParse(suffix[(comma + 1)..], out minor) && major >= 0 && minor >= 0;
    }
}
