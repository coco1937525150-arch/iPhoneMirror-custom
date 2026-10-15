using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Windows;

/// <summary>
/// A visual editor for the actual recording composition. The layout is
/// normalized to the output canvas, not the imported background image pixels.
/// This keeps tiny background pictures from degrading the phone image.
/// </summary>
internal sealed class RecordingLayoutEditorWindow : Window
{
    private readonly Grid _preview = new();
    private readonly Canvas _overlay = new();
    private readonly Image _phone = new() { Stretch = Stretch.Fill, Cursor = Cursors.SizeAll };
    private readonly Image _backgroundImage = new()
    {
        Stretch = Stretch.UniformToFill, ClipToBounds = true,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _readout = new() { TextWrapping = TextWrapping.Wrap };
    private readonly BitmapSource _shell;
    private readonly bool _landscape;
    private const double ZoomStep = 0.05;
    private double _backgroundWidth = 1280, _backgroundHeight = 720;
    private double _centerX = .5, _centerY = .5, _scale = 1.0;
    private int _rotationDegrees;
    private bool _dragging;
    private Point _dragOffset;

    internal RecordingLayoutEditorWindow(string backgroundPath, bool landscape = false,
        uint sourceWidth = 1280, uint sourceHeight = 720)
    {
        _landscape = landscape;
        _rotationDegrees = landscape ? 90 : 0;
        // With no custom background, the recording canvas is the capture
        // resolution selected in the settings window, never a fixed 1280x720.
        _backgroundWidth = Math.Max(160U, sourceWidth);
        _backgroundHeight = Math.Max(160U, sourceHeight);
        Title = "编辑录制布局 — 拖动手机壳 / 鼠标滚轮缩放";
        Width = 1040;
        Height = 740;
        MinWidth = 610;
        MinHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var asset = PhoneShellAssetStore.ActivePath;
        if (!File.Exists(asset))
            throw new FileNotFoundException("内置手机壳资源缺失，无法打开布局编辑器。", asset);
        _shell = LoadImage(asset);
        var settings = (Application.Current as App)?.UpdateSettings;
        if (settings is not null)
        {
            _centerX = double.IsFinite(settings.MediaOutputShellCenterX)
                ? settings.MediaOutputShellCenterX : .5;
            _centerY = double.IsFinite(settings.MediaOutputShellCenterY)
                ? settings.MediaOutputShellCenterY : .5;
            _scale = double.IsFinite(settings.MediaOutputShellScale)
                ? Math.Clamp(settings.MediaOutputShellScale, .1, 4) : 1;
            _rotationDegrees = PhoneShellMediaComposer.NormalizeRotation(
                settings.MediaOutputShellRotationDegrees ?? _rotationDegrees);
        }

        _preview.Background = Brushes.LightGray;
        if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
        {
            _backgroundImage.Source = LoadImage(backgroundPath);
            _overlay.Background = Brushes.Transparent;
        }
        else
            _overlay.Background = Brushes.White;
        _preview.ClipToBounds = true;
        _preview.Children.Add(_backgroundImage);
        _overlay.ClipToBounds = true;
        _overlay.IsHitTestVisible = true;
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        _overlay.VerticalAlignment = VerticalAlignment.Center;
        _preview.Children.Add(_overlay);
        // The editor and FFmpeg composer share the same persisted rotation.
        UpdatePhoneImage();
        _overlay.Children.Add(_phone);
        _phone.MouseLeftButtonDown += StartDrag;
        _phone.MouseMove += ContinueDrag;
        _phone.MouseLeftButtonUp += EndDrag;
        _overlay.MouseWheel += Zoom;
        _preview.SizeChanged += (_, _) => Redraw();

        var save = new Button { Content = "保存布局", Padding = new Thickness(22, 10, 22, 10), Margin = new Thickness(6) };
        save.Click += (_, _) =>
        {
            // Re-clamp even a layout loaded from older, unbounded versions.
            ConstrainLayout();
            if (Application.Current is App app)
            {
                app.UpdateSettings.MediaOutputShellCenterX = _centerX;
                app.UpdateSettings.MediaOutputShellCenterY = _centerY;
                app.UpdateSettings.MediaOutputShellScale = _scale;
                app.UpdateSettings.MediaOutputShellRotationDegrees = _rotationDegrees;
                if (!app.SaveUpdateSettings())
                {
                    MessageBox.Show(this, "无法保存布局配置。", "保存失败");
                    return;
                }
            }
            DialogResult = true;
        };
        var reset = new Button { Content = "居中 / 重置", Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(6) };
        reset.Click += (_, _) => { _centerX = _centerY = .5; _scale = 1; Redraw(); };
        var plus = new Button { Content = "放大 +", Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(6) };
        plus.Click += (_, _) => AdjustZoom(1);
        var minus = new Button { Content = "缩小 −", Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(6) };
        minus.Click += (_, _) => AdjustZoom(-1);
        var rotateLeft = new Button { Content = "左旋 90°", Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(4) };
        rotateLeft.Click += (_, _) => Rotate(-90);
        var rotateRight = new Button { Content = "右旋 90°", Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(4) };
        rotateRight.Click += (_, _) => Rotate(90);
        var resetDirection = new Button { Content = "方向 0°", Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(4) };
        resetDirection.Click += (_, _) => { _rotationDegrees = 0; UpdatePhoneImage(); Redraw(); };
        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(6) };
        cancel.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_readout);
        actions.Children.Add(minus);
        actions.Children.Add(plus);
        actions.Children.Add(rotateLeft);
        actions.Children.Add(rotateRight);
        actions.Children.Add(resetDirection);
        actions.Children.Add(reset);
        actions.Children.Add(cancel);
        actions.Children.Add(save);

        var host = new Grid { Margin = new Thickness(16) };
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var frame = new Border
        {
            Child = _preview,
            Background = Brushes.White,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 10)
        };
        host.Children.Add(frame);
        Grid.SetRow(actions, 1);
        host.Children.Add(actions);
        Content = host;
        Loaded += (_, _) => Redraw();
    }

    private static BitmapSource LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path));
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void UpdatePhoneImage()
    {
        _phone.Source = _rotationDegrees == 0 ? _shell :
            new TransformedBitmap(_shell, new RotateTransform(_rotationDegrees));
    }

    private void Rotate(int delta)
    {
        _rotationDegrees = PhoneShellMediaComposer.NormalizeRotation(
            _rotationDegrees + delta);
        UpdatePhoneImage();
        Redraw();
    }

    private Rect ConstrainLayout()
    {
        // Compute in original image pixels, not WPF preview units; the same
        // routine is used when generating the actual MP4 shell overlay.
        var reference = DeviceFrameProfileResolver.ActiveIPhoneShell
            .GetOuterDimensions(1320, 2868);
        var aspect = _rotationDegrees % 180 != 0
            ? reference.Height / (double)reference.Width
            : reference.Width / (double)reference.Height;
        var fit = PhoneShellMediaComposer.FitInsideCanvas(
            _backgroundWidth, _backgroundHeight, aspect,
            _centerX, _centerY, _scale);
        _centerX = fit.CenterX;
        _centerY = fit.CenterY;
        _scale = fit.Scale;
        return fit.Bounds;
    }

    private void Redraw()
    {
        var bounds = ConstrainLayout();
        if (_preview.ActualWidth <= 0 || _preview.ActualHeight <= 0) return;
        var factor = Math.Min(_preview.ActualWidth / _backgroundWidth,
            _preview.ActualHeight / _backgroundHeight);
        var displayedWidth = _backgroundWidth * factor;
        var displayedHeight = _backgroundHeight * factor;
        _overlay.Width = displayedWidth;
        _overlay.Height = displayedHeight;
        _backgroundImage.Width = displayedWidth;
        _backgroundImage.Height = displayedHeight;
        _phone.Width = bounds.Width * factor;
        _phone.Height = bounds.Height * factor;
        Canvas.SetLeft(_phone, bounds.X * factor);
        Canvas.SetTop(_phone, bounds.Y * factor);
        _readout.Text = $"画布 {_backgroundWidth:0}×{_backgroundHeight:0} · 比例 {_scale:P0} · 位置 {_centerX:P0}, {_centerY:P0} · 旋转 {_rotationDegrees}° · 安全边距 5px";
    }

    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        var pointer = e.GetPosition(_overlay);
        _dragOffset = new Point(pointer.X - _centerX * _overlay.Width,
            pointer.Y - _centerY * _overlay.Height);
        _phone.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueDrag(object sender, MouseEventArgs e)
    {
        if (!_dragging || _overlay.Width <= 0 || _overlay.Height <= 0) return;
        var p = e.GetPosition(_overlay);
        _centerX = (p.X - _dragOffset.X) / _overlay.Width;
        _centerY = (p.Y - _dragOffset.Y) / _overlay.Height;
        Redraw();
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _phone.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void AdjustZoom(int steps)
    {
        if (steps == 0) return;
        // Five PERCENTAGE POINTS per click/notch, not a multiplicative 10%
        // jump. Keep the existing .1..4 limits and layout-safety clamp.
        _scale = Math.Clamp(
            Math.Round(_scale + steps * ZoomStep, 3, MidpointRounding.AwayFromZero),
            .1, 4);
        Redraw();
    }

    private void Zoom(object sender, MouseWheelEventArgs e)
    {
        var notches = Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120);
        AdjustZoom(notches);
        e.Handled = true;
    }
}
