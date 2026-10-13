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
/// normalized to the background image, not the preview-window pixel size.
/// This keeps drag/zoom stable for arbitrary original-size backgrounds.
/// </summary>
internal sealed class RecordingLayoutEditorWindow : Window
{
    private readonly Grid _preview = new();
    private readonly Canvas _overlay = new();
    private readonly Image _phone = new() { Stretch = Stretch.Fill, Cursor = Cursors.SizeAll };
    private readonly TextBlock _readout = new() { TextWrapping = TextWrapping.Wrap };
    private readonly BitmapSource _shell;
    private double _backgroundWidth = 1280, _backgroundHeight = 720;
    private double _centerX = .5, _centerY = .5, _scale = 1.0;
    private bool _dragging;
    private Point _dragOffset;

    internal RecordingLayoutEditorWindow(string backgroundPath)
    {
        Title = "编辑录制布局 — 拖动手机壳 / 鼠标滚轮缩放";
        Width = 1040;
        Height = 740;
        MinWidth = 610;
        MinHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var asset = Path.Combine(AppContext.BaseDirectory, "Assets", "iphone16_pro_max_shell.png");
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
        }

        var previewImage = new Image { Stretch = Stretch.Uniform };
        if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
        {
            var background = LoadImage(backgroundPath);
            _backgroundWidth = background.PixelWidth;
            _backgroundHeight = background.PixelHeight;
            previewImage.Source = background;
        }
        else
        {
            _preview.Background = Brushes.White;
        }
        _preview.ClipToBounds = true;
        _preview.Children.Add(previewImage);
        _overlay.ClipToBounds = false;
        _overlay.IsHitTestVisible = true;
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        _overlay.VerticalAlignment = VerticalAlignment.Center;
        _preview.Children.Add(_overlay);
        _phone.Source = _shell;
        _overlay.Children.Add(_phone);
        _phone.MouseLeftButtonDown += StartDrag;
        _phone.MouseMove += ContinueDrag;
        _phone.MouseLeftButtonUp += EndDrag;
        _overlay.MouseWheel += Zoom;
        _preview.SizeChanged += (_, _) => Redraw();

        var save = new Button { Content = "保存布局", Padding = new Thickness(22, 10, 22, 10), Margin = new Thickness(6) };
        save.Click += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.UpdateSettings.MediaOutputShellCenterX = _centerX;
                app.UpdateSettings.MediaOutputShellCenterY = _centerY;
                app.UpdateSettings.MediaOutputShellScale = _scale;
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
        plus.Click += (_, _) => { _scale = Math.Min(4, _scale * 1.1); Redraw(); };
        var minus = new Button { Content = "缩小 −", Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(6) };
        minus.Click += (_, _) => { _scale = Math.Max(.1, _scale / 1.1); Redraw(); };
        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(6) };
        cancel.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_readout);
        actions.Children.Add(minus);
        actions.Children.Add(plus);
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

    private void Redraw()
    {
        if (_preview.ActualWidth <= 0 || _preview.ActualHeight <= 0) return;
        var factor = Math.Min(_preview.ActualWidth / _backgroundWidth,
            _preview.ActualHeight / _backgroundHeight);
        var displayedWidth = _backgroundWidth * factor;
        var displayedHeight = _backgroundHeight * factor;
        _overlay.Width = displayedWidth;
        _overlay.Height = displayedHeight;

        // Match PhoneShellMediaComposer's reference geometry exactly.
        var reference = DeviceFrameProfileResolver.IPhoneDynamicIsland.GetOuterDimensions(1320, 2868);
        var aspect = reference.Width / (double)reference.Height;
        var baseHeight = Math.Min(displayedHeight * .94, displayedWidth * .94 / aspect);
        var shellHeight = baseHeight * _scale;
        var shellWidth = shellHeight * aspect;
        _phone.Width = shellWidth;
        _phone.Height = shellHeight;
        Canvas.SetLeft(_phone, displayedWidth * _centerX - shellWidth / 2);
        Canvas.SetTop(_phone, displayedHeight * _centerY - shellHeight / 2);
        _readout.Text = $"画布 {_backgroundWidth:0}×{_backgroundHeight:0} · 比例 {_scale:P0} · 位置 {_centerX:P0}, {_centerY:P0}";
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

    private void Zoom(object sender, MouseWheelEventArgs e)
    {
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), .1, 4);
        Redraw();
        e.Handled = true;
    }
}
