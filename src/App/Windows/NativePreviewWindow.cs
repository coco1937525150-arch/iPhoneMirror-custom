using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

/// <summary>
/// Native top-level DirectComposition preview. WS_EX_NOREDIRECTIONBITMAP keeps
/// DWM from allocating a second WPF redirection surface, allowing the native
/// renderer to attach its visual tree directly to this HWND.
/// </summary>
internal sealed class NativePreviewWindow : IDisposable
{
    internal const string StableTitle = "iPhoneMirror OBS Preview";
    private static readonly HashSet<NativePreviewWindow> OpenWindows = [];
    private static int BossKeyHidden;

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WmContextMenu = 0x007B;
    private const int WmNcLeftButtonDoubleClick = 0x00A3;
    private const int WmNcRightButtonDown = 0x00A4;
    private const int WmNcRightButtonUp = 0x00A5;
    private const int WmRightButtonDown = 0x0204;
    private const int WmRightButtonUp = 0x0205;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmClose = 0x0010;
    private const int WmEraseBackground = 0x0014;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMiddleButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmSetCursor = 0x0020;
    private const int WmKillFocus = 0x0008;
    private const int WmCancelMode = 0x001F;
    private const int WmActivateApp = 0x001C;
    private const int WmCaptureChanged = 0x0215;
    private const int WmSetIcon = 0x0080;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmDpiChanged = 0x02E0;
    private const int VkEscape = 0x1B;
    private const int VkReturn = 0x0D;
    private const int VkF11 = 0x7A;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int GwlStyle = -16;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsCaption = 0x00C00000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsThickFrame = 0x00040000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoRedirectionBitmap = 0x00200000;

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 2;
    private const uint CursorShowing = 0x00000001;
    private static readonly nint HwndTopMost = new(-1);
    private static readonly nint HwndNoTopMost = new(-2);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmDoNotRound = 1;
    private const int DwmRound = 2;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private readonly HwndSource _source;
    private readonly AspectRatioWindowController _aspectController;
    private Func<nint, bool> _attachPreview;
    private Action<nint> _detachPreview;
    private Func<nint, bool> _refreshPreview;
    private readonly ContextMenu _contextMenu;
    private readonly MenuItem _fullScreenItem;
    private readonly MenuItem _windowMenuItem;
    private readonly MenuItem _windowSizeItem;
    private readonly List<(double Fraction, MenuItem Item)> _sizePresets = [];
    private readonly MenuItem _displayMenuItem;
    private readonly MenuItem _topMostItem;
    private readonly MenuItem _fixedItem;
    private readonly MenuItem? _reverseControlMenuItem;
    private readonly MenuItem? _bluetoothControlItem;
    private readonly MenuItem? _usbControlItem;
    private readonly MenuItem? _wirelessControlItem;
    private readonly MenuItem? _cornerItem;
    private readonly MenuItem _muteMenuItem;
    private readonly MenuItem _muteThisItem;
    private readonly MenuItem _muteOthersItem;
    private readonly MenuItem _rotateLeftItem;
    private readonly MenuItem _rotateRightItem;
    private readonly MenuItem _closeItem;
    private readonly MenuItem? _imageSettingsItem;
    private readonly MenuItem? _projectionSettingsItem;
    private readonly Func<bool>? _isAudioEnabled;
    private readonly Func<int>? _connectedDeviceCount;
    private readonly Action<bool>? _setAudioEnabled;
    private readonly Action? _muteOtherWindows;
    private readonly Action<nint>? _showImageSettings;
    private readonly Action? _showProjectionSettings;
    private readonly Func<nint, bool>? _isReverseControlEnabled;
    private readonly Func<bool>? _isReverseControlHotkeyRegistered;
    private readonly Action<PreviewPointerEventArgs>? _pointerInput;
    private readonly Action<PreviewKeyboardEventArgs>? _keyboardInput;
    private readonly Action<nint>? _requestReverseControl;
    private readonly Func<bool>? _isUsbControlEnabled;
    private readonly Action<nint>? _requestUsbControl;
    private readonly Action<nint>? _requestWirelessControl;
    private readonly Action<string>? _logDiagnostic;
    private ulong _sessionHandle;
    private readonly string _windowTitle;
    private readonly double _cornerRadius;
    private readonly double _cornerExponent;
    private DeviceFrameProfile _deviceFrameProfile;
    private readonly Border? _managedContentRoot;
    private readonly FrameworkElement? _managedContent;
    private readonly Transform? _managedContentOriginalLayoutTransform;
    private readonly Action? _managedContentDetached;
    private uint _sourceWidth;
    private uint _sourceHeight;
    private nint _handle;
    private bool _attached;
    private bool _isFullScreen;
    private double _sizeFraction = 0.82;
    private double _wheelZoomTargetFraction = 0.82;
    private readonly DispatcherTimer _wheelZoomTimer;
    private readonly DispatcherTimer _rotationTimer;
    private readonly Stopwatch _rotationClock = Stopwatch.StartNew();
    private PhoneRotationTimeline _rotationTimeline = new();
    private bool _rotationAnimating;
    private readonly DispatcherTimer _airPlayOrientationTimer;
    private bool _autoRotateAirPlay;
    private bool _autoAirPlayWasLandscape;
    private WindowRect _rotationOriginalBounds;
    private int _rotationOriginalClientWidth;
    private int _rotationOriginalClientHeight;
    private float _rotationCanvasWidth;
    private float _rotationCanvasHeight;
    private float _rotationScale = 1.0f;
    private bool _disposed;
    private bool _closeQueued;
    private bool _isTopMost;
    private bool _isFixed;
    private bool _cornersEnabled = true;
    private int _rotation;
    private byte _capturedMouseButtons;
    private nint _largeIcon;
    private nint _smallIcon;
    private ProtectedContentOverlayWindow? _protectedOverlay;
    private WindowRect _restoreRectangle;
    private nint _restoreStyle;

    internal bool IsFullScreen => _isFullScreen;
    internal event Action<bool>? FullScreenChanged;

    private NativePreviewWindow(uint sourceWidth, uint sourceHeight, string title,
        Func<nint, bool> attachPreview, Action<nint> detachPreview,
        Func<nint, bool> refreshPreview, ulong sessionHandle,
         double cornerRadius, double cornerExponent, DeviceFrameProfile deviceFrameProfile,
         Func<bool>? isAudioEnabled = null, Func<int>? connectedDeviceCount = null,
         Action<bool>? setAudioEnabled = null, Action? muteOtherWindows = null,
          Action<nint>? showImageSettings = null, Action? showProjectionSettings = null,
           Func<nint, bool>? isReverseControlEnabled = null,
           Action<PreviewPointerEventArgs>? pointerInput = null,
           Action<PreviewKeyboardEventArgs>? keyboardInput = null,
           Action<nint>? requestReverseControl = null,
           Func<bool>? isReverseControlHotkeyRegistered = null,
           Func<bool>? isUsbControlEnabled = null,
           Action<nint>? requestUsbControl = null,
           Action<nint>? requestWirelessControl = null,
           FrameworkElement? managedContent = null, Action? managedContentDetached = null,
         Action<string>? logDiagnostic = null)
    {
        _attachPreview = attachPreview;
        _detachPreview = detachPreview;
        _refreshPreview = refreshPreview;
        _sessionHandle = sessionHandle;
        _cornerRadius = cornerRadius;
        _cornerExponent = cornerExponent;
        _deviceFrameProfile = managedContent is null ? deviceFrameProfile : DeviceFrameProfile.None;
        _isAudioEnabled = isAudioEnabled;
        _connectedDeviceCount = connectedDeviceCount;
        _setAudioEnabled = setAudioEnabled;
        _muteOtherWindows = muteOtherWindows;
        _showImageSettings = showImageSettings;
        _showProjectionSettings = showProjectionSettings;
        _isReverseControlEnabled = isReverseControlEnabled;
        _isReverseControlHotkeyRegistered = isReverseControlHotkeyRegistered;
        _pointerInput = pointerInput;
        _keyboardInput = keyboardInput;
        _requestReverseControl = requestReverseControl;
        _isUsbControlEnabled = isUsbControlEnabled;
        _requestUsbControl = requestUsbControl;
        _requestWirelessControl = requestWirelessControl;
        _logDiagnostic = logDiagnostic;
        _managedContent = managedContent;
        _managedContentOriginalLayoutTransform = managedContent?.LayoutTransform;
        _managedContentDetached = managedContentDetached;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _contextMenu = new ContextMenu
        {
            Style = (Style)Application.Current.FindResource("DeviceContextMenuStyle"),
            Placement = PlacementMode.MousePoint,
        };
        var itemStyle = (Style)Application.Current.FindResource("DeviceMenuItemStyle");
        var submenuStyle = (Style)Application.Current.FindResource("DeviceSubmenuItemStyle");
        _fullScreenItem = new MenuItem { Style = itemStyle };
        _fullScreenItem.Click += (_, _) => ToggleFullScreen();
        _windowMenuItem = new MenuItem { Style = submenuStyle };
        _windowSizeItem = new MenuItem { Style = submenuStyle };
        foreach (var fraction in new[] { 0.40, 0.55, 0.70, 0.85, 0.95 })
        {
            var preset = new MenuItem
            {
                Header = $"{fraction:P0}",
                Style = itemStyle,
                IsCheckable = true,
            };
            var selectedFraction = fraction;
            preset.Click += (_, _) => SetIndependentWindowSize(selectedFraction);
            _sizePresets.Add((fraction, preset));
            _windowSizeItem.Items.Add(preset);
        }
        _topMostItem = new MenuItem { Style = itemStyle, IsCheckable = true };
        _topMostItem.Click += (_, _) => ToggleTopMost();
        _fixedItem = new MenuItem { Style = itemStyle };
        _fixedItem.Click += (_, _) => ToggleFixedWindow();
        if (_requestReverseControl is not null)
        {
            _reverseControlMenuItem = new MenuItem { Style = submenuStyle };
            _bluetoothControlItem = new MenuItem { Style = itemStyle };
            _bluetoothControlItem.Click += (_, _) => EnableReverseControl();
            _reverseControlMenuItem.Items.Add(_bluetoothControlItem);
        }
        if (_requestUsbControl is not null)
        {
            _usbControlItem = new MenuItem { Style = itemStyle };
            _usbControlItem.Click += (_, _) => _requestUsbControl(_handle);
            _reverseControlMenuItem?.Items.Add(_usbControlItem);
        }
        if (_requestWirelessControl is not null)
        {
            _wirelessControlItem = new MenuItem { Style = itemStyle };
            _wirelessControlItem.Click += (_, _) => _requestWirelessControl(_handle);
            _reverseControlMenuItem?.Items.Add(_wirelessControlItem);
        }
        _windowMenuItem.Items.Add(_topMostItem);
        _windowMenuItem.Items.Add(_fixedItem);
        _windowMenuItem.Items.Add(_windowSizeItem);
        _displayMenuItem = new MenuItem { Style = submenuStyle };
        _muteMenuItem = new MenuItem { Style = submenuStyle };
        _muteThisItem = new MenuItem { Style = itemStyle };
        _muteThisItem.Click += (_, _) => ToggleWindowAudio();
        _muteOthersItem = new MenuItem { Style = itemStyle };
        _muteOthersItem.Click += (_, _) => MuteOtherWindows();
        _muteMenuItem.Items.Add(_muteThisItem);
        _muteMenuItem.Items.Add(_muteOthersItem);
        _rotateLeftItem = new MenuItem { Style = itemStyle };
        _rotateLeftItem.Click += (_, _) => Rotate(-1);
        _rotateRightItem = new MenuItem { Style = itemStyle };
        _rotateRightItem.Click += (_, _) => Rotate(1);
        _closeItem = new MenuItem { Style = itemStyle };
        _closeItem.Click += (_, _) => QueueClose();
        if (managedContent is null)
        {
            _cornerItem = new MenuItem { Style = itemStyle };
            _cornerItem.Click += (_, _) => ToggleCorners();
            if (_showImageSettings is not null)
            {
                _imageSettingsItem = new MenuItem { Style = itemStyle };
                _imageSettingsItem.Click += (_, _) => ShowImageSettings();
                _displayMenuItem.Items.Add(_imageSettingsItem);
            }
            _displayMenuItem.Items.Add(_cornerItem);
            if (_showProjectionSettings is not null)
            {
                _projectionSettingsItem = new MenuItem { Style = itemStyle };
                _projectionSettingsItem.Click += (_, _) => ShowProjectionSettings();
            }
        }
        _displayMenuItem.Items.Add(_rotateLeftItem);
        _displayMenuItem.Items.Add(_rotateRightItem);
        _contextMenu.Items.Add(_fullScreenItem);
        _contextMenu.Items.Add(_windowMenuItem);
        _contextMenu.Items.Add(_displayMenuItem);
        if (_reverseControlMenuItem is not null) _contextMenu.Items.Add(_reverseControlMenuItem);
        if (_setAudioEnabled is not null) _contextMenu.Items.Add(_muteMenuItem);
        if (_projectionSettingsItem is not null) _contextMenu.Items.Add(_projectionSettingsItem);
        _contextMenu.Items.Add(new Separator
        {
            Style = (Style)Application.Current.FindResource("DeviceMenuSeparatorStyle"),
        });
        _contextMenu.Items.Add(_closeItem);
        UpdateContextMenuLabels();
        var windowTitle = string.IsNullOrWhiteSpace(title) ? StableTitle : title;
        _windowTitle = windowTitle;
        var parameters = new HwndSourceParameters(windowTitle)
        {
            Width = 720,
            Height = 900,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = managedContent is null
                ? WsPopup | WsThickFrame | WsSysMenu | WsMinimizeBox |
                    WsClipChildren | WsClipSiblings
                : WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox |
                    WsMaximizeBox | WsClipChildren | WsClipSiblings,
            ExtendedWindowStyle = WsExAppWindow |
                (managedContent is null ? WsExNoRedirectionBitmap : 0),
            TreatAsInputRoot = true,
        };
        _source = new HwndSource(parameters);
        _handle = _source.Handle;
        if (_handle == 0) throw new InvalidOperationException(
            "Could not create the native preview window.");
        _wheelZoomTimer = new DispatcherTimer(DispatcherPriority.Render, _source.Dispatcher)
        {
            // Coalesce high-resolution trackpad and wheel events; keep native
            // HWND/D3D11 resize traffic lower than the desktop refresh rate.
            Interval = TimeSpan.FromMilliseconds(24),
        };
        _wheelZoomTimer.Tick += OnWheelZoomTick;
        _rotationTimer = new DispatcherTimer(DispatcherPriority.Render, _source.Dispatcher)
        {
            // The reference CSS rotates the whole phone continuously over
            // 1000ms. Update the DComp transform at up to 120Hz instead of
            // visibly stepping once every ~16ms on high-refresh monitors.
            Interval = TimeSpan.FromMilliseconds(8),
        };
        _rotationTimer.Tick += OnRotationTick;
        _airPlayOrientationTimer = new DispatcherTimer(
            DispatcherPriority.Background, _source.Dispatcher)
        {
            // AirPlay may briefly change decoded dimensions during playback
            // negotiation; rotate only after the orientation has been stable.
            Interval = TimeSpan.FromMilliseconds(450),
        };
        _airPlayOrientationTimer.Tick += OnAirPlayOrientationSettled;

        if (managedContent is not null)
        {
            _managedContentRoot = new Border
            {
                Background = Brushes.Black,
                Child = managedContent,
                ClipToBounds = true,
            };
            _source.RootVisual = _managedContentRoot;
        }

        _ = SetWindowTextW(_handle, windowTitle);
        ApplyApplicationIcons();
        if (managedContent is null)
        {
            var cornerPreference = DwmDoNotRound;
            _ = DwmSetWindowAttribute(_handle, DwmWindowCornerPreference,
                ref cornerPreference, sizeof(int));
            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttributeColor(_handle, DwmBorderColor,
                ref borderColor, sizeof(uint));
        }
        else
        {
            // Media-cast content uses a normal captioned HWND. Explicitly ask
            // DWM for the Windows default corner treatment so it remains
            // rounded even when the app's theme changes window preferences.
            var cornerPreference = DwmRound;
            _ = DwmSetWindowAttribute(_handle, DwmWindowCornerPreference,
                ref cornerPreference, sizeof(int));
            var darkTitleBar = 1;
            _ = DwmSetWindowAttribute(_handle, DwmUseImmersiveDarkMode,
                ref darkTitleBar, sizeof(int));
        }

        var initialWindowDimensions = _deviceFrameProfile.GetOuterDimensions(sourceWidth, sourceHeight);
        _aspectController = new AspectRatioWindowController(_source,
            initialWindowDimensions.Width, initialWindowDimensions.Height,
            () => !_disposed && !_rotationAnimating && !_isFullScreen &&
                _handle != 0 && !IsIconic(_handle) && !IsZoomed(_handle));
        // Install the instance hook only after every callback dependency is
        // initialized; HwndSource construction itself dispatches messages.
        _source.AddHook(WindowProcedure);
        OpenWindows.Add(this);
        if (managedContent is null)
        {
            // Preserve the original borderless DirectComposition window for
            // wired/wireless mirroring. Video casting alone uses system chrome.
            _ = SetWindowPos(_handle, 0, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        Log("independent_window_created",
            ("mode", WindowMode), ("handle", AppLog.Handle(_sessionHandle)),
            ("size", $"{sourceWidth}x{sourceHeight}"),
            ("title_bar", managedContent is not null));
    }

    internal event EventHandler? Closed;
    internal ulong SessionHandle => _sessionHandle;
    // Match the native renderer: when AirPlay already supplies landscape
    // pixels we rotate only the physical bezel, never the input twice.
    private int EffectiveSourceTurns => (_rotation & 1) != 0 &&
        _sourceWidth > _sourceHeight ? 0 : _rotation;

    internal (uint Width, uint Height, int Rotation) ControlGeometry =>
        (EffectiveSourceTurns & 1) == 0
            ? (_sourceWidth, _sourceHeight, EffectiveSourceTurns)
            : (_sourceHeight, _sourceWidth, EffectiveSourceTurns);

    internal void SetWaitingForWirelessReconnect(bool waiting)
    {
        if (_disposed || _handle == 0 || _managedContent is not null) return;
        // Never present a stale frame as a live iPhone feed. The native session
        // is already gone; only the user's window geometry is retained.
        var title = waiting
            ? _windowTitle + " · " + LocalizationService.Get("StatusWaitingDevice")
            : _windowTitle;
        _ = SetWindowTextW(_handle, title);
        Log("independent_window_wireless_reconnect_wait",
            ("waiting", waiting), ("mode", WindowMode));
    }

    internal bool RebindSession(ulong sessionHandle)
    {
        if (_disposed || _managedContent is not null || _handle == 0 || sessionHandle == 0)
            return false;
        // The replacement D3D11 renderer owns a different visual; do not
        // transfer an in-flight old-session DComp animation across handles.
        if (_rotationAnimating) AbortRotationAnimation();

        var previousHandle = _sessionHandle;
        if (_attached)
        {
            try { _detachPreview(_handle); }
            catch { /* The old native session may already be gone. */ }
            _attached = false;
        }

        _sessionHandle = sessionHandle;
        _attachPreview = hwnd => NativeCore.AttachDevicePreview(sessionHandle, hwnd);
        _detachPreview = hwnd => NativeCore.DetachDevicePreview(sessionHandle, hwnd);
        _refreshPreview = hwnd => NativeCore.AttachDevicePreview(sessionHandle, hwnd);
        if (!_attachPreview(_handle))
        {
            Log("independent_window_rebind_failed",
                ("mode", WindowMode), ("old_handle", AppLog.Handle(previousHandle)),
                ("new_handle", AppLog.Handle(sessionHandle)));
            return false;
        }

        _attached = true;
        _ = NativeCore.SetDeviceWindowCornerProfile(sessionHandle, _handle,
            _cornersEnabled ? _cornerRadius : 0, _cornerExponent);
        ApplyDeviceFrameProfile();
        if (_rotation != 0 &&
            !NativeCore.SetDeviceWindowRotation(sessionHandle, _handle, _rotation))
            Log("independent_window_rotation_rebind_failed",
                ("quarter_turns", _rotation));
        Log("independent_window_rebound",
            ("mode", WindowMode), ("old_handle", AppLog.Handle(previousHandle)),
            ("new_handle", AppLog.Handle(sessionHandle)));
        return true;
    }

    private string WindowMode => _managedContent is null ? "device" : "media_cast";

    private void Log(string eventName, params (string Key, object? Value)[] fields)
    {
        try
        {
            _logDiagnostic?.Invoke(AppLog.Event(eventName,
                fields.Select(field => (object?)field).ToArray()));
        }
        catch (Exception error)
        {
            // Diagnostics must never interfere with HWND ownership or teardown.
            DiagnosticLogger.ExceptionOnce("preview-log-callback", "logging",
                "preview_log_callback_failed", error);
        }
    }

    internal static bool TryCreateAndShowForSession(ulong handle, uint sourceWidth,
        uint sourceHeight, string title, double cornerRadius, double cornerExponent,
        DeviceFrameProfile deviceFrameProfile,
        Func<bool> isAudioEnabled, Func<int> connectedDeviceCount,
        Action<bool> setAudioEnabled, Action muteOtherWindows,
        Action<nint> showImageSettings, Action showProjectionSettings,
        out NativePreviewWindow? window, Action<string>? logDiagnostic = null,
        Func<nint, bool>? isReverseControlEnabled = null,
        Action<PreviewPointerEventArgs>? pointerInput = null,
        Action<PreviewKeyboardEventArgs>? keyboardInput = null,
        Action<nint>? requestReverseControl = null,
        Func<bool>? isReverseControlHotkeyRegistered = null,
        Func<bool>? isUsbControlEnabled = null,
        Action<nint>? requestUsbControl = null,
        Action<nint>? requestWirelessControl = null)
    {
        window = null;
        NativePreviewWindow? candidate = null;
        try
        {
            candidate = new NativePreviewWindow(sourceWidth, sourceHeight, title,
                hwnd => NativeCore.AttachDevicePreview(handle, hwnd),
                hwnd => NativeCore.DetachDevicePreview(handle, hwnd),
                hwnd => NativeCore.AttachDevicePreview(handle, hwnd),
                 handle, cornerRadius, cornerExponent, deviceFrameProfile, isAudioEnabled,
                 connectedDeviceCount, setAudioEnabled, muteOtherWindows,
                  showImageSettings, showProjectionSettings,
                  isReverseControlEnabled, pointerInput, keyboardInput, requestReverseControl,
                  isReverseControlHotkeyRegistered, isUsbControlEnabled, requestUsbControl,
                  requestWirelessControl,
                  logDiagnostic: logDiagnostic);
            if (!candidate._attachPreview(candidate._handle))
            {
                logDiagnostic?.Invoke(AppLog.Event("independent_window_attach_failed",
                    ("mode", "device"), ("handle", AppLog.Handle(handle))));
                candidate.Dispose();
                return false;
            }
            candidate._attached = true;
            candidate.ApplyDeviceFrameProfile();
            candidate.ShowInitially();
            if (Volatile.Read(ref BossKeyHidden) != 0)
                candidate.SetBossKeyHidden(true);
            else
                _ = SetForegroundWindow(candidate._handle);
            window = candidate;
            logDiagnostic?.Invoke(AppLog.Event("independent_window_shown",
                ("mode", "device"), ("handle", AppLog.Handle(handle))));
            return true;
        }
        catch (Exception error)
        {
            logDiagnostic?.Invoke(AppLog.Event("independent_window_create_failed",
                ("mode", "device"), ("handle", AppLog.Handle(handle)),
                ("error", AppLog.Error(error))));
            candidate?.Dispose();
            return false;
        }
    }

    internal static bool TryCreateAndShowForContent(FrameworkElement content,
        uint sourceWidth, uint sourceHeight, string title,
        Func<bool> isAudioEnabled, Action<bool> setAudioEnabled,
        Func<int> connectedDeviceCount, Action muteOtherWindows,
        Action contentDetached, out NativePreviewWindow? window,
        Action<string>? logDiagnostic = null)
    {
        window = null;
        NativePreviewWindow? candidate = null;
        try
        {
            candidate = new NativePreviewWindow(sourceWidth, sourceHeight, title,
                _ => true, _ => { }, _ =>
                 {
                     content.InvalidateVisual();
                     return true;
                 }, 0, 0, 1, DeviceFrameProfile.None, isAudioEnabled, connectedDeviceCount, setAudioEnabled,
                 muteOtherWindows, null, null,
                 managedContent: content, managedContentDetached: contentDetached,
                 logDiagnostic: logDiagnostic);
            candidate._attached = true;
            candidate.ShowInitially();
            if (Volatile.Read(ref BossKeyHidden) != 0)
                candidate.SetBossKeyHidden(true);
            else
                _ = SetForegroundWindow(candidate._handle);
            window = candidate;
            logDiagnostic?.Invoke(AppLog.Event("independent_window_shown",
                ("mode", "media_cast"), ("handle", AppLog.Handle(0))));
            return true;
        }
        catch (Exception error)
        {
            logDiagnostic?.Invoke(AppLog.Event("independent_window_create_failed",
                ("mode", "media_cast"), ("error", AppLog.Error(error))));
            if (candidate is not null) candidate.Dispose();
            else
            {
                if (content.Parent is Decorator owner) owner.Child = null;
                contentDetached();
            }
            return false;
        }
    }

    private void ShowInitially()
    {
        var boundsApplied = _aspectController.ApplyInitialBounds();
        var preferredTopMost = Application.Current is App app &&
            app.UpdateSettings.IndependentWindowAlwaysOnTop;
        var zOrder = preferredTopMost ? HwndTopMost : HwndNoTopMost;
        var positionApplied = SetWindowPos(_handle, zOrder, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
        if (!positionApplied)
        {
            // Keep a usable independent window if shell positioning fails.
            _ = ShowWindow(_handle, SwShow);
            positionApplied = SetWindowPos(_handle, zOrder, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate);
        }
        _isTopMost = preferredTopMost && positionApplied;
        UpdateContextMenuLabels();
        Log("independent_window_initial_layout",
            ("mode", WindowMode), ("bounds_applied", boundsApplied),
            ("top_most", _isTopMost));
    }

    internal void Activate()
    {
        if (_disposed || _handle == 0) return;
        if (IsIconic(_handle)) _ = ShowWindow(_handle, SwRestore);
        else _ = ShowWindow(_handle, SwShow);
        if (!_attached && _attachPreview(_handle))
            _attached = true;
        else if (_attached)
            _ = _attachPreview(_handle);
        _ = SetForegroundWindow(_handle);
        _ = SetFocus(_handle);
        Log("independent_window_activated",
            ("mode", WindowMode), ("attached", _attached),
            ("full_screen", _isFullScreen));
    }

    internal void HideForShutdown()
    {
        if (_disposed || _handle == 0) return;
        _contextMenu.IsOpen = false;
        _contextMenu.PlacementTarget = null;
        _ = ShowWindow(_handle, SwHide);
    }

    internal void SetBossKeyHidden(bool hidden)
    {
        if (_disposed || _handle == 0) return;
        if (hidden)
        {
            _contextMenu.IsOpen = false;
            _contextMenu.PlacementTarget = null;
            _ = ShowWindow(_handle, SwHide);
            return;
        }

        _ = ShowWindow(_handle, SwShow);
    }

    internal static void SetAllBossKeyHidden(bool hidden)
    {
        Volatile.Write(ref BossKeyHidden, hidden ? 1 : 0);
        foreach (var window in OpenWindows.ToArray())
            window.SetBossKeyHidden(hidden);
    }

    internal bool RefreshPreview()
    {
        var refreshed = !_disposed && _handle != 0 && _refreshPreview(_handle);
        Log("independent_window_refresh",
            ("mode", WindowMode), ("success", refreshed));
        return refreshed;
    }

    internal void SetProtectedContent(bool protectedContent, string audioDisplay)
    {
        if (_disposed || _handle == 0 || _managedContent is not null) return;
        if (!protectedContent)
        {
            _protectedOverlay?.Close();
            _protectedOverlay = null;
            return;
        }
        if (_protectedOverlay is null)
            _protectedOverlay = ProtectedContentOverlayWindow.ShowFor(_handle,
                audioDisplay);
        else
            _protectedOverlay.UpdateAudioDisplay(audioDisplay);
    }

    internal void SetSourceDimensions(uint width, uint height)
    {
        var changed = _sourceWidth != width || _sourceHeight != height;
        _sourceWidth = width;
        _sourceHeight = height;
        if (changed && _autoRotateAirPlay && width != 0 && height != 0)
        {
            // Video playback is usually exposed as landscape-coded AirPlay
            // frames. Do not stretch the old portrait shell to the newly
            // reported source aspect before the phone-body animation begins.
            _airPlayOrientationTimer.Stop();
            _airPlayOrientationTimer.Start();
        }
        var pendingAutoTurn = _autoRotateAirPlay && width > 0 && height > 0 &&
            (width > height) != ((_rotation & 1) != 0);
        if (!_rotationAnimating && !pendingAutoTurn)
            ApplyRotatedDimensions();
        if (changed)
            Log("independent_window_dimensions",
                ("mode", WindowMode), ("size", $"{width}x{height}"),
                ("rotation", _rotation));
    }

    internal void SetDeviceFrameProfile(DeviceFrameProfile profile)
    {
        if (_disposed || _managedContent is not null || profile == _deviceFrameProfile) return;
        _deviceFrameProfile = profile;
        if (!_rotationAnimating) ApplyRotatedDimensions();
        ApplyDeviceFrameProfile();
        Log("independent_window_device_frame",
            ("mode", WindowMode), ("profile", profile.Id),
            ("enabled", profile.Enabled), ("cutout", profile.CutoutKind));
    }

    internal void ToggleFullScreen()
    {
        if (_disposed || _handle == 0) return;
        if (_rotationAnimating) CompleteRotationAnimation();
        if (_isFullScreen)
        {
            _isFullScreen = false;
            _ = RemovePropW(_handle, "iPhoneMirrorFullScreen");
            _ = SetWindowLongPtrW(_handle, GwlStyle, _restoreStyle);
            _ = SetWindowPos(_handle, 0, _restoreRectangle.Left, _restoreRectangle.Top,
                Math.Max(1, _restoreRectangle.Right - _restoreRectangle.Left),
                Math.Max(1, _restoreRectangle.Bottom - _restoreRectangle.Top),
                SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
            _aspectController.Reflow();
            _ = SetForegroundWindow(_handle);
            Log("independent_window_fullscreen",
                ("mode", WindowMode), ("enabled", false));
            FullScreenChanged?.Invoke(false);
            return;
        }

        if (!GetWindowRect(_handle, out _restoreRectangle)) return;
        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref monitorInfo)) return;

        _restoreStyle = GetWindowLongPtrW(_handle, GwlStyle);
        _isFullScreen = true;
        _ = SetPropW(_handle, "iPhoneMirrorFullScreen", (nint)1);
        var fullScreenStyle = _managedContent is null
            ? (nint)(_restoreStyle.ToInt64() & ~WsThickFrame)
            : (nint)(_restoreStyle.ToInt64() & ~(WsCaption | WsThickFrame));
        _ = SetWindowLongPtrW(_handle, GwlStyle, fullScreenStyle);
        _ = SetWindowPos(_handle, 0, monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top,
            SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
        _ = SetForegroundWindow(_handle);
        Log("independent_window_fullscreen",
            ("mode", WindowMode), ("enabled", true));
        FullScreenChanged?.Invoke(true);
    }

    private void ApplyApplicationIcons()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            ExtractIconExW(executable, 0, out _largeIcon, out _smallIcon, 1) == 0)
            return;
        if (_largeIcon != 0) _ = SendMessageW(_handle, WmSetIcon, 1, _largeIcon);
        if (_smallIcon != 0) _ = SendMessageW(_handle, WmSetIcon, 0, _smallIcon);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        if (WindowsAutoPlayGuard.ShouldCancel(message, _sessionHandle != 0))
        {
            handled = true;
            Log("autoplay_cancelled", ("message", "WM_QUERYCANCELAUTOPLAY"));
            return 1;
        }
        if (IsPointerInputEnabledForWindow &&
            (message == WmKillFocus || message == WmCancelMode ||
             message == WmCaptureChanged ||
             (message == WmActivateApp && wParam == 0)))
        {
            if (_capturedMouseButtons != 0)
            {
                _capturedMouseButtons = 0;
                _ = ReleaseCapture();
            }
            _pointerInput?.Invoke(new PreviewPointerEventArgs(
                PreviewPointerKind.Reset, 0, 0, 0, 0));
            _keyboardInput?.Invoke(new PreviewKeyboardEventArgs(
                PreviewKeyboardKind.Reset, 0));
            handled = true;
            return 0;
        }
        switch (message)
        {
            case WmMouseMove when IsPointerInputActive:
                DispatchPointer(PreviewPointerKind.Move, lParam, 0, 0);
                handled = true;
                return 0;
            case WmLeftButtonDown when IsPointerInputActive:
                _capturedMouseButtons |= 1;
                _ = SetCapture(hwnd);
                DispatchPointer(PreviewPointerKind.ButtonDown, lParam, 1, 0);
                handled = true;
                return 0;
            case WmLeftButtonUp when IsPointerInputActive:
                _capturedMouseButtons = (byte)(_capturedMouseButtons & ~1);
                DispatchPointer(PreviewPointerKind.ButtonUp, lParam, 1, 0);
                if (_capturedMouseButtons == 0) _ = ReleaseCapture();
                handled = true;
                return 0;
            case WmRightButtonDown when IsReverseControlEnabledForWindow:
                if (IsUsbControlEnabledForWindow) break;
                if (IsReverseControlActive)
                    DispatchPointer(PreviewPointerKind.ButtonDown, lParam, 2, 0);
                handled = true;
                return 0;
            case WmRightButtonUp when IsReverseControlEnabledForWindow:
                if (IsUsbControlEnabledForWindow) break;
                if (IsReverseControlActive)
                    DispatchPointer(PreviewPointerKind.ButtonUp, lParam, 2, 0);
                handled = true;
                return 0;
            case WmMiddleButtonDown when IsPointerInputActive:
                _capturedMouseButtons |= 4;
                _ = SetCapture(hwnd);
                DispatchPointer(PreviewPointerKind.ButtonDown, lParam, 4, 0);
                handled = true;
                return 0;
            case WmMiddleButtonUp when IsPointerInputActive:
                _capturedMouseButtons = (byte)(_capturedMouseButtons & ~4);
                DispatchPointer(PreviewPointerKind.ButtonUp, lParam, 4, 0);
                if (_capturedMouseButtons == 0) _ = ReleaseCapture();
                handled = true;
                return 0;
            case WmMouseWheel when !_isFullScreen && !_isFixed &&
                ((wParam.ToInt64() & 0x0008) != 0 || GetKeyState(VkControl) < 0):
                // CTRL+wheel is a Windows window-size gesture, not an iOS
                // scroll event. Handle it even while USB/BLE input is enabled.
                var ticks = unchecked((short)(wParam.ToInt64() >> 16));
                if (ticks != 0) QueueSmoothWheelZoom(ticks);
                handled = true;
                return 0;
            case WmMouseWheel when IsPointerInputActive:
                DispatchPointer(PreviewPointerKind.Wheel, ScreenPointToClientLParam(lParam), 0,
                    unchecked((short)(wParam.ToInt64() >> 16)));
                handled = true;
                return 0;
            case WmSetCursor when IsUsbControlEnabledForWindow:
                // USB touch control never captures the Windows pointer. A
                // previous reverse-control route may have left the process
                // cursor hidden, so reassert visibility on every cursor
                // negotiation for this independent HWND.
                ShowSystemCursor();
                handled = true;
                return 1;
            case WmSetCursor when IsReverseControlActive:
                HideSystemCursor();
                handled = true;
                return 1;
            case WmKeyDown or WmSysKeyDown when IsBossKeyHotkey(wParam.ToInt32()):
                // Boss key is process-global. Keep it out of the iPhone HID
                // stream while the main window receives WM_HOTKEY.
                handled = true;
                return 0;
            case WmKeyDown when !_rotationAnimating && GetKeyState(VkControl) < 0 &&
                wParam.ToInt32() == VkLeft:
                handled = true;
                Rotate(-1);
                return 0;
            case WmKeyDown when !_rotationAnimating && GetKeyState(VkControl) < 0 &&
                wParam.ToInt32() == VkRight:
                handled = true;
                Rotate(1);
                return 0;
            case WmKeyDown when _rotationAnimating && GetKeyState(VkControl) < 0 &&
                (wParam.ToInt32() == VkLeft || wParam.ToInt32() == VkRight):
                handled = true;
                Rotate(wParam.ToInt32() == VkRight ? 1 : -1);
                return 0;
            case WmKeyDown when wParam.ToInt32() == VkF11:
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmKeyDown when wParam.ToInt32() == VkEscape && _isFullScreen:
                // Escape exits the preview window's full-screen mode even
                // while reverse control is forwarding other key presses.
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmKeyDown when IsPointerInputActive:
                _keyboardInput?.Invoke(new PreviewKeyboardEventArgs(
                    PreviewKeyboardKind.Down, wParam.ToInt32(),
                    (int)((lParam.ToInt64() >> 16) & 0x1FF)));
                handled = true;
                return 0;
            case WmSysKeyDown when IsPointerInputActive:
                _keyboardInput?.Invoke(new PreviewKeyboardEventArgs(
                    PreviewKeyboardKind.Down, wParam.ToInt32(),
                    (int)((lParam.ToInt64() >> 16) & 0x1FF)));
                handled = true;
                return 0;
            case WmKeyUp when IsPointerInputActive:
            case WmSysKeyUp when IsPointerInputActive:
                _keyboardInput?.Invoke(new PreviewKeyboardEventArgs(
                    PreviewKeyboardKind.Up, wParam.ToInt32(),
                    (int)((lParam.ToInt64() >> 16) & 0x1FF)));
                handled = true;
                return 0;
            case WmContextMenu when IsReverseControlEnabledForWindow:
            case WmNcRightButtonDown when IsReverseControlEnabledForWindow:
            case WmNcRightButtonUp when IsReverseControlEnabledForWindow:
                handled = true;
                return 0;
            case WmNcCalcSize when _managedContent is null:
                handled = true;
                return 0;
            case WmNcHitTest when _managedContent is null:
                handled = true;
                return _isFullScreen || _isFixed ? HtClient : HitTestWindow(lParam);
            case WmContextMenu:
            case WmRightButtonUp:
                handled = true;
                ShowContextMenu();
                return 0;
            case WmNcRightButtonDown when _managedContent is null:
            case WmNcRightButtonUp when _managedContent is null:
            case WmRightButtonDown when _managedContent is null:
                handled = true;
                ShowContextMenu();
                return 0;
            case WmNcLeftButtonDoubleClick when _managedContent is null &&
                wParam.ToInt32() == HtCaption:
                handled = true;
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Input, ToggleFullScreen);
                return 0;
            case WmLeftButtonDoubleClick:
                handled = true;
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Input, ToggleFullScreen);
                return 0;
            case WmEraseBackground:
                // The native DirectComposition visual paints the complete
                // client. Suppressing class-background erase avoids a white
                // flash while its swap chain is being resized/recreated.
                handled = true;
                return 1;
            case WmClose:
                handled = true;
                QueueClose();
                return 0;
            case WmSysKeyDown when wParam.ToInt32() == VkReturn && GetKeyState(VkMenu) < 0:
                handled = true;
                ToggleFullScreen();
                return 0;
            case WmDpiChanged:
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    _aspectController.Reflow);
                break;
        }
        return 0;
    }

    private nint HitTestWindow(nint packedScreenPoint)
    {
        if (_handle == 0 || !GetWindowRect(_handle, out var bounds)) return HtClient;
        var packed = packedScreenPoint.ToInt64();
        var x = (short)(packed & 0xFFFF);
        var y = (short)((packed >> 16) & 0xFFFF);
        var dpi = GetDpiForWindow(_handle);
        var border = Math.Max(6, (int)Math.Round(8.0 * (dpi == 0 ? 1.0 : dpi / 96.0)));

        var left = x < bounds.Left + border;
        var right = x >= bounds.Right - border;
        var top = y < bounds.Top + border;
        var bottom = y >= bounds.Bottom - border;
        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return HtCaption;
    }

    private void QueueClose()
    {
        if (_disposed || _closeQueued) return;
        _closeQueued = true;
        Log("independent_window_close_queued", ("mode", WindowMode));
        try
        {
            _source.Dispatcher.BeginInvoke(DispatcherPriority.Send, Dispose);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher can enter shutdown between WM_CLOSE and the
            // queued callback. Dispose synchronously while the HWND is still
            // on this thread instead of surfacing an unhandled hook error.
            try { Dispose(); }
            catch (Exception error)
            {
                Log("independent_window_close_failed", ("error", AppLog.Error(error)));
            }
        }
    }

    private void SetIndependentWindowSize(double fraction)
    {
        if (_disposed || _isFullScreen || _isFixed || _handle == 0) return;
        _wheelZoomTimer.Stop();
        fraction = Math.Round(Math.Clamp(fraction, 0.35, 0.95), 2);
        if (!_aspectController.ResizeToWorkAreaFraction(fraction)) return;
        _sizeFraction = fraction;
        _wheelZoomTargetFraction = fraction;
        UpdateContextMenuLabels();
        Log("independent_window_size_changed",
            ("mode", WindowMode), ("fraction", fraction));
    }

    private void QueueSmoothWheelZoom(int wheelDelta)
    {
        if (_disposed || _isFullScreen || _isFixed || _handle == 0) return;
        // Preserve precision for touchpads which send sub-120 wheel deltas.
        // Subsequent input updates one target instead of immediately running
        // SetWindowPos once for every raw wheel event.
        _wheelZoomTargetFraction = Math.Clamp(
            _wheelZoomTargetFraction + wheelDelta / 120.0 * 0.035,
            0.35, 0.95);
        if (!_wheelZoomTimer.IsEnabled) _wheelZoomTimer.Start();
    }

    private void OnWheelZoomTick(object? sender, EventArgs e)
    {
        if (_disposed || _isFullScreen || _isFixed || _handle == 0)
        {
            _wheelZoomTimer.Stop();
            return;
        }
        var remaining = _wheelZoomTargetFraction - _sizeFraction;
        var finished = Math.Abs(remaining) < 0.001;
        var next = finished
            ? _wheelZoomTargetFraction
            : _sizeFraction + remaining * 0.38;
        if (!_aspectController.ResizeToWorkAreaFraction(next))
        {
            _wheelZoomTimer.Stop();
            _wheelZoomTargetFraction = _sizeFraction;
            return;
        }
        _sizeFraction = next;
        if (!finished) return;
        _wheelZoomTimer.Stop();
        UpdateContextMenuLabels();
        Log("independent_window_size_changed",
            ("mode", WindowMode), ("fraction", Math.Round(next, 3)));
    }

    private void ShowContextMenu()
    {
        if (_disposed) return;
        UpdateContextMenuLabels();
        _contextMenu.IsOpen = true;
        Log("independent_window_context_menu",
            ("mode", WindowMode), ("full_screen", _isFullScreen),
            ("top_most", _isTopMost), ("fixed", _isFixed));
    }

    private void UpdateContextMenuLabels()
    {
        _fullScreenItem.Header = LocalizationService.Get(
            _isFullScreen ? "IndependentWindowExitFullScreen" :
                "IndependentWindowEnterFullScreen");
        _windowMenuItem.Header = LocalizationService.Get("IndependentWindowWindowMenu");
        _windowSizeItem.Header = LocalizationService.Get("IndependentWindowSize");
        _windowSizeItem.IsEnabled = !_isFullScreen && !_isFixed;
        foreach (var (fraction, item) in _sizePresets)
            item.IsChecked = Math.Abs(fraction - _sizeFraction) < 0.001;
        _displayMenuItem.Header = LocalizationService.Get("IndependentWindowDisplayMenu");
        _topMostItem.Header = LocalizationService.Get(
            _isTopMost ? "IndependentWindowUnpin" : "IndependentWindowPin");
        _topMostItem.IsChecked = _isTopMost;
        _fixedItem.Header = LocalizationService.Get(
            _isFixed ? "IndependentWindowUnfix" : "IndependentWindowFix");
        _fixedItem.IsEnabled = !_isFullScreen;
        if (_reverseControlMenuItem is not null)
        {
            _reverseControlMenuItem.Header = LocalizationService.Get(
                "IndependentWindowReverseControl");
            _reverseControlMenuItem.IsEnabled = !_isFullScreen;
        }
        if (_bluetoothControlItem is not null)
        {
            _bluetoothControlItem.Header = LocalizationService.Get(
                "IndependentWindowBluetoothControl");
            _bluetoothControlItem.IsEnabled = !_isFullScreen;
        }
        if (_usbControlItem is not null)
        {
            _usbControlItem.Header = LocalizationService.Get(
                "IndependentWindowWiredProjection");
            _usbControlItem.IsEnabled = !_isFullScreen;
        }
        if (_wirelessControlItem is not null)
        {
            _wirelessControlItem.Header = LocalizationService.Get(
                "IndependentWindowWirelessProjection");
            _wirelessControlItem.IsEnabled = !_isFullScreen;
        }
        if (_cornerItem is not null)
            _cornerItem.Header = LocalizationService.Get(
                _cornersEnabled ? "IndependentWindowRemoveCorners" :
                    "IndependentWindowKeepCorners");
        if (_imageSettingsItem is not null)
            _imageSettingsItem.Header = LocalizationService.Get(
                "IndependentWindowImageSettings");
        if (_projectionSettingsItem is not null)
            _projectionSettingsItem.Header = LocalizationService.Get(
                "IndependentWindowProjectionSettings");
        _rotateLeftItem.Header = LocalizationService.Get("IndependentWindowRotateLeft");
        _rotateRightItem.Header = LocalizationService.Get("IndependentWindowRotateRight");
        _muteMenuItem.Header = LocalizationService.Get("IndependentWindowAudioMenu");
        _muteThisItem.Header = LocalizationService.Get(
            _isAudioEnabled?.Invoke() == false
                ? "IndependentWindowUnmuteThis"
                : "IndependentWindowMuteThis");
        _muteOthersItem.Header = LocalizationService.Get("IndependentWindowMuteOthers");
        _muteOthersItem.Visibility = IndependentWindowAudioPolicy.ShowMuteOthers(
            _connectedDeviceCount?.Invoke() ?? 1)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _closeItem.Header = LocalizationService.Get("IndependentWindowClose");
    }

    private void ToggleWindowAudio()
    {
        if (_disposed || _isAudioEnabled is null || _setAudioEnabled is null) return;
        var enabled = !_isAudioEnabled();
        _setAudioEnabled(enabled);
        UpdateContextMenuLabels();
        Log("independent_window_audio",
            ("mode", WindowMode), ("enabled", enabled));
    }

    private void MuteOtherWindows()
    {
        if (_disposed || _muteOtherWindows is null) return;
        _muteOtherWindows();
        UpdateContextMenuLabels();
        Log("independent_window_mute_others", ("mode", WindowMode));
    }

    private void ShowImageSettings()
    {
        if (_disposed || _showImageSettings is null) return;
        _showImageSettings(_handle);
        Log("independent_window_image_settings", ("mode", WindowMode));
    }

    private void ShowProjectionSettings()
    {
        if (_disposed || _showProjectionSettings is null) return;
        _showProjectionSettings();
        Log("independent_window_projection_settings", ("mode", WindowMode));
    }

    private void ToggleTopMost()
    {
        if (_disposed || _handle == 0) return;
        var desiredTopMost = !_isTopMost;
        if (!SetWindowPos(_handle, desiredTopMost ? HwndTopMost : HwndNoTopMost,
                0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate))
        {
            Log("independent_window_topmost_failed",
                ("mode", WindowMode), ("requested", desiredTopMost),
                ("win32_error", Marshal.GetLastWin32Error()));
            UpdateContextMenuLabels();
            return;
        }
        _isTopMost = desiredTopMost;
        if (Application.Current is App app)
        {
            // Each open window retains its own state; persist the selected
            // default for the next independent window or application launch.
            app.UpdateSettings.IndependentWindowAlwaysOnTop = _isTopMost;
            _ = app.SaveUpdateSettings();
        }
        UpdateContextMenuLabels();
        Log("independent_window_topmost",
            ("mode", WindowMode), ("enabled", _isTopMost));
    }

    private void ToggleFixedWindow()
    {
        if (_disposed || _handle == 0 || _isFullScreen) return;
        _isFixed = !_isFixed;
        var style = GetWindowLongPtrW(_handle, GwlStyle).ToInt64();
        style = _isFixed ? style & ~WsThickFrame : style | WsThickFrame;
        _ = SetWindowLongPtrW(_handle, GwlStyle, (nint)style);
        _ = SetWindowPos(_handle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        if (!_isFixed) _aspectController.Reflow();
        UpdateContextMenuLabels();
        Log("independent_window_fixed",
            ("mode", WindowMode), ("enabled", _isFixed));
    }

    private bool IsReverseControlActive => _pointerInput is not null &&
        (_isReverseControlEnabled?.Invoke(_handle) ?? false) &&
        GetForegroundWindow() == _handle;
    private bool IsReverseControlEnabledForWindow => _pointerInput is not null &&
        (_isReverseControlEnabled?.Invoke(_handle) ?? false);
    private bool IsUsbControlEnabledForWindow => _pointerInput is not null &&
        (_isUsbControlEnabled?.Invoke() ?? false);
    private bool IsPointerInputEnabledForWindow =>
        IsReverseControlEnabledForWindow || IsUsbControlEnabledForWindow;
    private bool IsPointerInputActive =>
        !_rotationAnimating && (IsReverseControlActive ||
        (IsUsbControlEnabledForWindow && GetForegroundWindow() == _handle));

    private void EnableReverseControl()
    {
        if (_disposed || _isFullScreen) return;
        EnsureFixedWindow();
        _requestReverseControl?.Invoke(_handle);
        UpdateContextMenuLabels();
    }

    private static bool IsBossKeyHotkey(int virtualKey) =>
        GetConfiguredShortcut(BluetoothShortcutAction.BossKey)
            .MatchesVirtualKey(virtualKey,
                IsKeyDown(VkControl), IsKeyDown(VkMenu), IsKeyDown(VkShift));

    private static KeyboardShortcut GetConfiguredShortcut(
        BluetoothShortcutAction action) =>
        Application.Current is App app
            ? KeyboardShortcut.FromSettings(app.UpdateSettings, action)
            : KeyboardShortcut.DefaultFor(action);

    private static bool IsKeyDown(int virtualKey) => GetKeyState(virtualKey) < 0;

    private void HideSystemCursor()
    {
        // ShowCursor uses one process-wide display counter. Other native
        // windows or overlays can increment it after control is enabled, so
        // assert hidden state whenever this independent HWND owns the cursor.
        var cursor = new CursorInfo
        {
            Size = (uint)Marshal.SizeOf<CursorInfo>(),
        };
        if (!GetCursorInfo(ref cursor))
        {
            Log("independent_cursor_query_failed",
                ("win32_error", Marshal.GetLastWin32Error()));
            // The caller still consumes WM_SETCURSOR. Do not change
            // ShowCursor's display count when it cannot be queried: repeated
            // cursor messages would otherwise leave it hidden after reverse
            // control has been turned off.
            return;
        }
        if ((cursor.Flags & CursorShowing) != 0)
            while (ShowCursor(false) >= 0) { }
    }

    private void ShowSystemCursor()
    {
        var cursor = new CursorInfo
        {
            Size = (uint)Marshal.SizeOf<CursorInfo>(),
        };
        if (!GetCursorInfo(ref cursor))
        {
            Log("independent_cursor_query_failed",
                ("win32_error", Marshal.GetLastWin32Error()));
            return;
        }
        if ((cursor.Flags & CursorShowing) == 0)
            while (ShowCursor(true) < 0) { }
    }

    private void EnsureFixedWindow()
    {
        if (_isFixed) return;
        ToggleFixedWindow();
    }

    internal void PrepareForUsbControl()
    {
        if (_disposed || _handle == 0) return;
        if (!_isFixed) ToggleFixedWindow();
        if (!_isTopMost) ToggleTopMost();
        _ = SetForegroundWindow(_handle);
        UpdateContextMenuLabels();
    }

    private nint ScreenPointToClientLParam(nint lParam)
    {
        var packed = lParam.ToInt64();
        var point = new NativePoint
        {
            X = unchecked((short)(packed & 0xFFFF)),
            Y = unchecked((short)((packed >> 16) & 0xFFFF)),
        };
        if (!ScreenToClient(_handle, ref point)) return lParam;
        var repacked = (point.X & 0xFFFFL) | ((point.Y & 0xFFFFL) << 16);
        return (nint)repacked;
    }

    private void DispatchPointer(PreviewPointerKind kind, nint lParam,
        byte button, int wheel)
    {
        if (_pointerInput is null || !GetClientRect(_handle, out var rect)) return;
        var packed = lParam.ToInt64();
        var x = (int)unchecked((short)(packed & 0xFFFF));
        var y = (int)unchecked((short)((packed >> 16) & 0xFFFF));
        var clientWidth = Math.Max(1, rect.Right - rect.Left);
        var clientHeight = Math.Max(1, rect.Bottom - rect.Top);
        var screen = _isFullScreen
            ? new DeviceScreenRect(0, 0, clientWidth, clientHeight)
            : _deviceFrameProfile.GetScreenRect(clientWidth, clientHeight);

        // The hardware shell is window chrome, not part of the iOS touch
        // surface. A new press/wheel in the bezel is ignored. Once a drag is
        // captured, however, clamp motion and the eventual button-up to the
        // nearest screen edge so the remote iPhone can never be left with a
        // stuck pointer/button state merely because the cursor crossed the
        // decorative shell.
        var insideScreen = screen.Contains(x, y);
        var capturedDrag = _capturedMouseButtons != 0;
        if (kind != PreviewPointerKind.Reset && !insideScreen &&
            !capturedDrag && kind != PreviewPointerKind.ButtonUp) return;
        x -= screen.X;
        y -= screen.Y;
        x = Math.Clamp(x, 0, Math.Max(0, screen.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, screen.Height - 1));

        var sourceTurns = EffectiveSourceTurns;
        var sourceWidth = (sourceTurns & 1) == 0 ? _sourceWidth : _sourceHeight;
        var sourceHeight = (sourceTurns & 1) == 0 ? _sourceHeight : _sourceWidth;
        _pointerInput(new PreviewPointerEventArgs(kind, (short)x, (short)y, button, wheel,
            screen.Width, screen.Height, sourceWidth, sourceHeight, sourceTurns));
    }

    private void ToggleCorners()
    {
        if (_managedContent is not null || _sessionHandle == 0 || _handle == 0) return;
        _cornersEnabled = !_cornersEnabled;
        _ = NativeCore.SetDeviceWindowCornerProfile(_sessionHandle, _handle,
            _cornersEnabled ? _cornerRadius : 0, _cornerExponent);
        UpdateContextMenuLabels();
        Log("independent_window_corners",
            ("mode", WindowMode), ("enabled", _cornersEnabled));
    }

    private void Rotate(int delta)
    {
        if (_disposed || _handle == 0) return;
        if (_managedContent is not null || _sessionHandle == 0 ||
            _isFullScreen || IsIconic(_handle))
        {
            // Media-cast windows have a separate managed visual and retain
            // the previous non-phone quarter-turn behavior.
            if (_managedContent is null) return;
            _rotation = ((_rotation + delta) % 4 + 4) % 4;
            _managedContent.LayoutTransform = new RotateTransform(_rotation * 90);
            ApplyRotatedDimensions();
            return;
        }

        // Windows reduced-motion setting skips interpolation but still keeps
        // the final shell and video under the same discrete native transform.
        if (!SystemParameters.MenuAnimation)
        {
            var next = ((_rotation + delta) % 4 + 4) % 4;
            if (NativeCore.SetDeviceWindowRotation(_sessionHandle, _handle, next))
            {
                _rotation = next;
                ApplyRotatedDimensions();
            }
            return;
        }

        if (!_rotationAnimating && !BeginRotationAnimation()) return;
        // Retarget from the CURRENT eased angle. A reverse click during the
        // animation does not snap back to zero or restart its phone texture.
        _rotationTimeline.Retarget(delta * 90.0, _rotationClock.Elapsed);
        UpdateRotationVisual();
        _rotationTimer.Start();
    }

    private bool BeginRotationAnimation()
    {
        if (!GetWindowRect(_handle, out var outer) ||
            !GetClientRect(_handle, out var client))
            return false;
        var clientW = client.Right - client.Left;
        var clientH = client.Bottom - client.Top;
        if (clientW <= 0 || clientH <= 0) return false;
        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return false;
        var borderW = outer.Right - outer.Left - clientW;
        var borderH = outer.Bottom - outer.Top - clientH;
        var available = Math.Min(info.WorkArea.Right - info.WorkArea.Left - borderW,
            info.WorkArea.Bottom - info.WorkArea.Top - borderH);
        if (available <= 0) return false;
        // The diagonal bounds EVERY angle (not merely portrait/landscape).
        var diagonal = Math.Sqrt((double)clientW * clientW +
            (double)clientH * clientH) + 4;
        var side = Math.Max(1, Math.Min((int)Math.Ceiling(diagonal), available));
        var scale = (float)Math.Min(1.0, (side - 1.0) / diagonal);
        if (scale <= 0.0f) return false;
        if (!NativeCore.SetDeviceWindowVisualRotation(_sessionHandle, _handle,
                0, side, side, scale, true))
        {
            Log("independent_window_rotation_visual_unavailable");
            return false;
        }

        var centerX = ((long)outer.Left + outer.Right) / 2;
        var centerY = ((long)outer.Top + outer.Bottom) / 2;
        var outerSideW = side + borderW;
        var outerSideH = side + borderH;
        var stageX = (int)Math.Clamp(centerX - outerSideW / 2L,
            info.WorkArea.Left, Math.Max(info.WorkArea.Left, info.WorkArea.Right - outerSideW));
        var stageY = (int)Math.Clamp(centerY - outerSideH / 2L,
            info.WorkArea.Top, Math.Max(info.WorkArea.Top, info.WorkArea.Bottom - outerSideH));
        // A reflow queued BEFORE the animation may otherwise wake up just
        // after the final geometry is committed, inflating the phone shell.
        _aspectController.CancelQueuedResize();
        // Set the animation guard BEFORE SetWindowPos dispatches synchronous
        // WM_SIZE/WM_WINDOWPOSCHANGED. A queued aspect-controller reflow used
        // to resize the temporary square while the device was mid-turn.
        _rotationOriginalBounds = outer;
        _rotationOriginalClientWidth = clientW;
        _rotationOriginalClientHeight = clientH;
        _rotationScale = scale;
        _rotationCanvasWidth = side;
        _rotationCanvasHeight = side;
        _rotationTimeline = new PhoneRotationTimeline();
        _rotationAnimating = true;
        if (!SetWindowPos(_handle, 0, stageX, stageY, outerSideW, outerSideH,
                SwpNoActivate | SwpNoZOrder))
        {
            _rotationAnimating = false;
            _ = NativeCore.SetDeviceWindowVisualRotation(_sessionHandle, _handle,
                0, 0, 0, 1, false);
            return false;
        }
        Log("independent_window_rotation_animation_started",
            ("duration_ms", 1000), ("canvas", side), ("scale", scale));
        return true;
    }

    private void OnRotationTick(object? sender, EventArgs e)
    {
        if (!_rotationAnimating || _disposed || _sessionHandle == 0)
        {
            _rotationTimer.Stop();
            return;
        }
        UpdateRotationVisual();
        if (_rotationTimeline.Finished(_rotationClock.Elapsed))
            CompleteRotationAnimation();
    }

    private void UpdateRotationVisual()
    {
        if (!_rotationAnimating) return;
        var angle = (float)_rotationTimeline.Sample(_rotationClock.Elapsed);
        if (!NativeCore.SetDeviceWindowVisualRotation(_sessionHandle, _handle,
                angle, _rotationCanvasWidth, _rotationCanvasHeight,
                _rotationScale, true))
        {
            Log("independent_window_rotation_animation_failed");
            AbortRotationAnimation();
        }
    }

    private void CompleteRotationAnimation()
    {
        if (!_rotationAnimating) return;
        _rotationTimer.Stop();
        var turns = (int)Math.Round(_rotationTimeline.TargetDegrees / 90.0);
        var nextRotation = ((_rotation + turns) % 4 + 4) % 4;
        var swapped = (turns & 1) != 0;
        var width = swapped ? _rotationOriginalClientHeight : _rotationOriginalClientWidth;
        var height = swapped ? _rotationOriginalClientWidth : _rotationOriginalClientHeight;
        var outerW = _rotationOriginalBounds.Right - _rotationOriginalBounds.Left -
            _rotationOriginalClientWidth;
        var outerH = _rotationOriginalBounds.Bottom - _rotationOriginalBounds.Top -
            _rotationOriginalClientHeight;
        var finalClientW = Math.Max(1, (int)Math.Round(width * _rotationScale));
        var finalClientH = Math.Max(1, (int)Math.Round(height * _rotationScale));
        var finalW = finalClientW + outerW;
        var finalH = finalClientH + outerH;
        var cx = ((long)_rotationOriginalBounds.Left + _rotationOriginalBounds.Right) / 2;
        var cy = ((long)_rotationOriginalBounds.Top + _rotationOriginalBounds.Bottom) / 2;
        var finalX = (int)(cx - finalW / 2L);
        var finalY = (int)(cy - finalH / 2L);

        // The reference animation rotates the WHOLE device inside a fixed
        // fitted stage. Keep the ORIGINAL portrait backbuffer untouched
        // until the last animation frame is visible. Only then resize HWND,
        // while DComp still presents the complete phone at the final angle.
        // The former order rotated the PNG shader inside a portrait swapchain
        // before the HWND was resized: landscape bezel + vertical screen.
        var finalAngle = (float)_rotationTimeline.TargetDegrees;
        if (!NativeCore.SetDeviceWindowVisualRotation(_sessionHandle, _handle,
                finalAngle, finalClientW, finalClientH, _rotationScale, true))
        {
            Log("independent_window_rotation_handoff_failed");
            AbortRotationAnimation();
            return;
        }
        if (!SetWindowPos(_handle, 0, finalX, finalY, finalW, finalH,
                SwpNoActivate | SwpNoZOrder))
        {
            AbortRotationAnimation();
            return;
        }
        // Commit the shader quarter-turn only AFTER the HWND has its final
        // geometry. Native D3D11 blocks intermediate presents until the new
        // swapchain size is ready, then clears DComp on the SAME present.
        if (!NativeCore.SetDeviceWindowRotation(_sessionHandle, _handle, nextRotation))
        {
            Log("independent_window_rotation_commit_failed");
            AbortRotationAnimation();
            return;
        }
        _rotation = nextRotation;
        _rotationAnimating = false;
        _aspectController.CancelQueuedResize();
        _ = NativeCore.SetDeviceWindowVisualRotation(_sessionHandle, _handle,
            0, 0, 0, 1, false);
        ApplyRotatedDimensions(reflow: false);
        Log("independent_window_rotation_animation_completed",
            ("mode", WindowMode), ("quarter_turns", nextRotation),
            ("duration_ms", 1000), ("client_size", $"{finalClientW}x{finalClientH}"));
    }

    private void AbortRotationAnimation()
    {
        if (!_rotationAnimating) return;
        _rotationTimer.Stop();
        _rotationAnimating = false;
        _aspectController.CancelQueuedResize();
        // Restore the unrotated HWND first; only then release DComp resize
        // protection. An early transform reset while still in the square
        // stage is visibly off-center and can briefly expose a giant bezel.
        var bounds = _rotationOriginalBounds;
        if (_handle != 0)
            _ = SetWindowPos(_handle, 0, bounds.Left, bounds.Top,
                Math.Max(1, bounds.Right - bounds.Left),
                Math.Max(1, bounds.Bottom - bounds.Top),
                SwpNoActivate | SwpNoZOrder);
        if (_sessionHandle != 0 && _handle != 0)
            _ = NativeCore.SetDeviceWindowVisualRotation(_sessionHandle, _handle,
                0, 0, 0, 1, false);
    }

    private void ApplyRotatedDimensions(bool reflow = true)
    {
        if (_sourceWidth == 0 || _sourceHeight == 0) return;
        // The SDK can ALREADY deliver landscape video after iOS rotates.
        // Normalise to a portrait physical phone first, then apply exactly
        // one shell rotation. Blindly swapping the new landscape source a
        // second time caused portrait-aspect HWND reflows / giant bezels.
        var portraitWidth = Math.Min(_sourceWidth, _sourceHeight);
        var portraitHeight = Math.Max(_sourceWidth, _sourceHeight);
        var width = (_rotation & 1) != 0 ? portraitHeight : portraitWidth;
        var height = (_rotation & 1) != 0 ? portraitWidth : portraitHeight;
        var outer = _deviceFrameProfile.GetOuterDimensions(width, height);
        _aspectController.SetSourceDimensions(outer.Width, outer.Height, reflow);
    }

    /// <summary>Automatically turn the entire physical AirPlay phone left
    /// when the decoded source switches to landscape; turn right on return
    /// to portrait. Does NOT attempt to infer playback from static pixels.</summary>
    internal void EnableAirPlayAutomaticRotation(bool enabled)
    {
        if (_managedContent is not null || _disposed) return;
        _autoRotateAirPlay = enabled;
        _airPlayOrientationTimer.Stop();
        if (enabled && _sourceWidth != 0 && _sourceHeight != 0)
            _airPlayOrientationTimer.Start();
    }

    private void OnAirPlayOrientationSettled(object? sender, EventArgs e)
    {
        _airPlayOrientationTimer.Stop();
        if (!_autoRotateAirPlay || _disposed || _handle == 0 ||
            _sessionHandle == 0 || _isFullScreen ||
            _sourceWidth == 0 || _sourceHeight == 0) return;

        // Ignore ambiguous nearly-square frames while the encoder negotiates.
        var largest = Math.Max(_sourceWidth, _sourceHeight);
        var smallest = Math.Min(_sourceWidth, _sourceHeight);
        if (smallest == 0 || largest < smallest * 1.12) return;
        var landscape = _sourceWidth > _sourceHeight;
        if (_autoAirPlayWasLandscape == landscape) return;
        // Manual rotation may already match the new physical orientation.
        // In that case update the automatic state without rotating TWICE.
        _autoAirPlayWasLandscape = landscape;
        var targetTurns = landscape ? 3 : 0;
        if ((_rotation & 3) == targetTurns && !_rotationAnimating)
            return;
        Log("independent_window_airplay_orientation_auto",
            ("landscape", landscape), ("source_width", _sourceWidth),
            ("source_height", _sourceHeight));
        Rotate(landscape ? -1 : 1);
    }

    private void ApplyDeviceFrameProfile()
    {
        if (_managedContent is not null || _sessionHandle == 0 || _handle == 0) return;
        _ = NativeCore.SetDeviceWindowFrameProfile(_sessionHandle, _handle, _deviceFrameProfile);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Log("independent_window_dispose_begin",
            ("mode", WindowMode), ("attached", _attached),
            ("full_screen", _isFullScreen));
        if (_rotationAnimating) AbortRotationAnimation();
        _disposed = true;
        _rotationTimer.Stop();
        _rotationTimer.Tick -= OnRotationTick;
        _airPlayOrientationTimer.Stop();
        _airPlayOrientationTimer.Tick -= OnAirPlayOrientationSettled;
        _wheelZoomTimer.Stop();
        _wheelZoomTimer.Tick -= OnWheelZoomTick;
        OpenWindows.Remove(this);
        _protectedOverlay?.Close();
        _protectedOverlay = null;
        _contextMenu.IsOpen = false;
        _contextMenu.PlacementTarget = null;
        _ = ShowWindow(_handle, SwHide);
        _aspectController.Dispose();
        if (_attached && _handle != 0)
        {
            // Isolate native detach failures so subsequent HWND/GDI cleanup still runs.
            try { _detachPreview(_handle); }
            catch (Exception ex) { Log("detach_preview_failed", ("error", ex.Message)); }
            _attached = false;
        }
        if (_managedContentRoot is not null)
        {
            if (_managedContent is not null)
                _managedContent.LayoutTransform =
                    _managedContentOriginalLayoutTransform ?? Transform.Identity;
            _managedContentRoot.Child = null;
            _source.RootVisual = null;
        }
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
        _handle = 0;
        if (_managedContentRoot is not null)
        {
            try { _managedContentDetached?.Invoke(); }
            catch { /* Window teardown remains best-effort. */ }
        }
        if (_largeIcon != 0) _ = DestroyIcon(_largeIcon);
        if (_smallIcon != 0 && _smallIcon != _largeIcon) _ = DestroyIcon(_smallIcon);
        _largeIcon = 0;
        _smallIcon = 0;
        Log("independent_window_disposed", ("mode", WindowMode));
        var closedHandlers = Closed;
        Closed = null;
        if (closedHandlers is null) return;
        foreach (EventHandler handler in closedHandlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception error)
            {
                Log("independent_window_closed_handler_failed",
                    ("mode", WindowMode), ("error", AppLog.Error(error)));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        internal uint Size;
        internal uint Flags;
        internal nint Cursor;
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal WindowRect Monitor;
        internal WindowRect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(nint window, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPropW(nint window, string name, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint RemovePropW(nint window, string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int iconIndex,
        out nint largeIcon, out nint smallIcon, uint iconCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute,
        ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeColor(nint window, int attribute,
        ref uint value, int valueSize);

}
