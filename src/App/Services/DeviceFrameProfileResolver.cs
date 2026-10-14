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
        0.030, 0.034, 0.034, 0.162,
        // Never overlay a second notch on the mirrored frame.
        DeviceCutoutKind.None, 0, 0, 0);

    internal static readonly DeviceFrameProfile IPhoneDynamicIsland = new(
        "iphone-dynamic-island", true,
        // Thin front-frame proportions of iPhone 16 Pro Max.
        0.064, 0.058, 0.058, 0.158,
        // Dynamic Island is already part of the decoded iOS picture.
        DeviceCutoutKind.None, 0, 0, 0);

    // Geometry calibrated against the opaque ring / transparent screen in the
    // immutable 994x2048 RGBA shell: center aperture approximately x=43..949,
    // y=39..2008. Ratios use DISPLAY short-edge units (not outer width).
    // Keep recorder, WPF layout editor and native preview on one profile.
    // The shell can no longer be overridden by files under LocalAppData.
    internal static readonly DeviceFrameProfile IPhoneImportedShell = new(
        "iphone-dynamic-island-custom-shell", true,
        0.048, 0.043, 0.043, 0.158,
        DeviceCutoutKind.None, 0, 0, 0);

    internal static DeviceFrameProfile ActiveIPhoneShell =>
        IPhoneImportedShell;

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
            return major >= 15 ? ActiveIPhoneShell : IPhoneNotch;
        }

        if (TryParseProductType(productType, "iPad", out _, out _)) return IPad;
        if (!string.IsNullOrWhiteSpace(productType)) return DeviceFrameProfile.None;

        if (frameWidth == 0 || frameHeight == 0) return DeviceFrameProfile.None;
        var shortEdge = Math.Min(frameWidth, frameHeight);
        var longEdge = Math.Max(frameWidth, frameHeight);
        var ratio = shortEdge / (double)longEdge;
        if (ratio is >= 0.38 and <= 0.56) return ActiveIPhoneShell;
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
