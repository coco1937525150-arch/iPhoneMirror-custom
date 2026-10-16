using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using Microsoft.Win32;

namespace IPhoneMirror.App.Windows;

public partial class MediaOutputSettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly bool _previewOnly;
    private bool _savePromptOpen;
    private bool _useRecommendedRecordingBitrate;
    private (uint Width, uint Height)? _lastDefaultSize;
    private string _customBackgroundPath = string.Empty;
    private bool _useDefaultWhiteBackground;
    private bool _restoringShellPreferences;
    private bool _savedRecordingResolution;
    private bool _savedStreamingResolution;

    internal MediaOutputSettingsWindow(MainViewModel viewModel, bool previewOnly = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _previewOnly = previewOnly;
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyPreviewOutputDefaults(force: true);
        RestoreShellPreferences();
        ShowBundledBuildIdentity();
        PhoneShellLayoutSettingsProvider.Current = () =>
        {
            if (Application.Current is not App app) return null;
            var p = app.UpdateSettings;
            return new PhoneShellLayoutSettings(p.MediaOutputShellCenterX,
                p.MediaOutputShellCenterY, p.MediaOutputShellScale,
                p.MediaOutputShellRotationDegrees);
        };
        RestoreOutputPreferences();
        if (Application.Current is App settingsApp)
            RecordingDirectoryBox.Text = settingsApp.UpdateSettings.MediaOutputRecordingDirectory;

        // Virtual camera resolution and FPS are restored with other output options.
        if (_previewOnly)
        {
            FeedbackText.Text = LocalizationService.Get("DeveloperReadOnlyPreview");
            UpdateStartButtons();
            return;
        }
        // Re-probe on each opening so a user can install FFmpeg or update PATH
        // without restarting the entire application.
        await RunAsync(() => _viewModel.EnsureMediaOutputCapabilitiesAsync(force: true));
        SelectFirstSupportedProtocol();
        UpdateStartButtons();
        if (!_viewModel.IsMediaOutputRunning &&
            _viewModel.PendingRecordingPath is not null)
            await PromptToSaveRecordingAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SaveShellPreferences();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CanStartMediaOutput) or
            nameof(MainViewModel.CanRecordMediaOutput) or
            nameof(MainViewModel.CanStreamRtmp) or
            nameof(MainViewModel.CanStreamSrt) or
            nameof(MainViewModel.CanStreamWhip) or
            nameof(MainViewModel.CanUseVirtualCamera) or
            nameof(MainViewModel.CanInstallVirtualCamera) or
            nameof(MainViewModel.CanUninstallVirtualCamera) or
            nameof(MainViewModel.CanStopMediaOutput))
            UpdateStartButtons();
        if (e.PropertyName == nameof(MainViewModel.SourceVideoHeight) &&
            !_viewModel.IsMediaOutputRunning)
            ApplyPreviewOutputDefaults(force: false);
        if (e.PropertyName == nameof(MainViewModel.IsMediaOutputRunning) &&
            !_viewModel.IsMediaOutputRunning &&
            _viewModel.PendingRecordingPath is not null)
            _ = Dispatcher.BeginInvoke(async () =>
                await PromptToSaveRecordingAsync());
    }

    private void ApplyPreviewOutputDefaults(bool force)
    {
        if (!IsLoaded && !force) return;
        var size = _viewModel.SuggestedMediaOutputSize();
        var previous = _lastDefaultSize;
        var updateRecording = !_savedRecordingResolution && (force || previous is null ||
            (RecordWidthBox.Text == previous.Value.Width.ToString() &&
             RecordHeightBox.Text == previous.Value.Height.ToString()));
        var updateStreaming = !_savedStreamingResolution && (force || previous is null ||
            (StreamWidthBox.Text == previous.Value.Width.ToString() &&
             StreamHeightBox.Text == previous.Value.Height.ToString()));
        var previousResolution = previous is null ? null :
            $"{previous.Value.Width}x{previous.Value.Height}";
        var selectedResolution =
            (VirtualCameraResolutionBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var updateVirtualCamera = force || previous is null ||
            string.Equals(selectedResolution, previousResolution,
                StringComparison.OrdinalIgnoreCase);

        if (updateRecording)
        {
            RecordWidthBox.Text = size.Width.ToString();
            RecordHeightBox.Text = size.Height.ToString();
        }
        if (updateStreaming)
        {
            StreamWidthBox.Text = size.Width.ToString();
            StreamHeightBox.Text = size.Height.ToString();
        }
        SelectVirtualCameraResolution(size.Width, size.Height,
            updateVirtualCamera);
        _lastDefaultSize = size;
    }

    private void ShowBundledBuildIdentity()
    {
        // BUILD_PROVENANCE.txt is injected into the exact publish directory
        // by Windows Actions AFTER compilation and bundled alongside EXE.
        // Showing its real run/commit lets the user distinguish a stale old
        // installation from the downloaded CI artifact; no version is guessed.
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "BUILD_PROVENANCE.txt");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            var commit = lines.FirstOrDefault(line =>
                line.StartsWith("Source Git commit: ", StringComparison.Ordinal));
            var workflow = lines.FirstOrDefault(line =>
                line.StartsWith("Git workflow run: ", StringComparison.Ordinal));
            if (commit is null || workflow is null) return;
            var sha = commit["Source Git commit: ".Length..].Trim();
            var run = workflow["Git workflow run: ".Length..].Split(' ')[0];
            if (sha.Length < 8 ||
                !ulong.TryParse(run, out _)) return;
            Pre6BuildIdentityText.Text =
                $"pre6 CI #{run} · {sha[..8]} · 测试版（未经过实机验收）";
            Pre6BuildIdentityText.Visibility = Visibility.Visible;
        }
        catch (Exception error)
        {
            DiagnosticLogger.ExceptionOnce("pre6-build-provenance", "media_output",
                "build_provenance_display_failed", error);
        }
    }

    private void SelectVirtualCameraResolution(uint width, uint height,
        bool updateSelection)
    {
        var value = $"{width}x{height}";
        var preset = VirtualCameraResolutionBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => !ReferenceEquals(
                    item, VirtualCameraCurrentResolutionItem) &&
                string.Equals(item.Tag as string, value,
                    StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            VirtualCameraCurrentResolutionItem.Visibility = Visibility.Collapsed;
            if (updateSelection) VirtualCameraResolutionBox.SelectedItem = preset;
            return;
        }

        VirtualCameraCurrentResolutionItem.Tag = value;
        VirtualCameraCurrentResolutionItem.Content = LocalizationService.Format(
            "CurrentPreviewResolutionFormat", width, height);
        VirtualCameraCurrentResolutionItem.Visibility = Visibility.Visible;
        if (updateSelection)
            VirtualCameraResolutionBox.SelectedItem =
                VirtualCameraCurrentResolutionItem;
    }

    private void RestoreShellPreferences()
    {
        if (Application.Current is not App app) return;
        var settings = app.UpdateSettings;
        _restoringShellPreferences = true;
        IncludePhoneShellBox.IsChecked = settings.MediaOutputIncludePhoneShell;
        TransparentShellRecordingBox.IsChecked =
            settings.MediaOutputTransparentShellRecording;
        if (TransparentMovQualityBox is not null)
            TransparentMovQualityBox.SelectedIndex =
                settings.MediaOutputTransparentMovQuality == 9 ? 0 :
                settings.MediaOutputTransparentMovQuality == 25 ? 2 : 1;
        _customBackgroundPath = settings.MediaOutputBackgroundImagePath ?? string.Empty;
        _useDefaultWhiteBackground = settings.MediaOutputUseDefaultWhiteBackground ??
            string.IsNullOrWhiteSpace(_customBackgroundPath);
        BackgroundImageBox.Text = _useDefaultWhiteBackground
            ? "默认白板（9:16）" : _customBackgroundPath;
        // Selecting a white background must not override a saved output size.
        _restoringShellPreferences = false;
        RefreshTransparentShellControls();
    }

    private void RestoreOutputPreferences()
    {
        if (Application.Current is not App app) return;
        var s = app.UpdateSettings;
        _savedRecordingResolution = ValidOutputSize(s.MediaOutputRecordWidth, s.MediaOutputRecordHeight);
        _savedStreamingResolution = ValidOutputSize(s.MediaOutputStreamWidth, s.MediaOutputStreamHeight);
        if (_savedRecordingResolution)
        {
            RecordWidthBox.Text = s.MediaOutputRecordWidth.ToString();
            RecordHeightBox.Text = s.MediaOutputRecordHeight.ToString();
            var tag = $"{Math.Min(s.MediaOutputRecordWidth, s.MediaOutputRecordHeight)}x" +
                $"{Math.Max(s.MediaOutputRecordWidth, s.MediaOutputRecordHeight)}";
            var selected = RecordResolutionPresetBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                ?? RecordResolutionPresetBox.Items.OfType<ComboBoxItem>().First();
            RecordResolutionPresetBox.SelectedItem = selected;
        }
        if (_savedStreamingResolution)
        {
            StreamWidthBox.Text = s.MediaOutputStreamWidth.ToString();
            StreamHeightBox.Text = s.MediaOutputStreamHeight.ToString();
        }
        RecordFpsBox.Text = s.MediaOutputRecordFps is >= 10 and <= 120 ?
            s.MediaOutputRecordFps.ToString() : "60";
        StreamFpsBox.Text = s.MediaOutputStreamFps is >= 10 and <= 60 ?
            s.MediaOutputStreamFps.ToString() : "60";
        RecordBitrateBox.Text = SafeBitrate(s.MediaOutputRecordBitrateKbps).ToString();
        if (s.MediaOutputRecordBitrateAutomatic)
        {
            var automatic = RecordBitrateBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, "recommend",
                    StringComparison.Ordinal));
            if (automatic is not null) RecordBitrateBox.SelectedItem = automatic;
        }
        StreamBitrateBox.Text = SafeBitrate(s.MediaOutputStreamBitrateKbps).ToString();
        var protocol = s.MediaOutputStreamProtocolIndex is >= 0 and <= 2 ?
            s.MediaOutputStreamProtocolIndex : 0;
        StreamProtocolBox.SelectedIndex = protocol;
        var cameraWidth = s.MediaOutputVirtualCameraWidth;
        var cameraHeight = s.MediaOutputVirtualCameraHeight;
        if (ValidOutputSize(cameraWidth, cameraHeight))
            SelectVirtualCameraResolution(cameraWidth, cameraHeight, updateSelection: true);
        VirtualCameraFpsBox.SelectedIndex = s.MediaOutputVirtualCameraFps == 60 ? 1 : 0;
    }

    private static bool ValidOutputSize(uint w, uint h) =>
        w is >= 160 and <= 4096 && h is >= 160 and <= 4096 &&
        (w & 1) == 0 && (h & 1) == 0;
    private static int SafeBitrate(int bitrate) =>
        bitrate is >= 500 and <= 150000 ? bitrate : 6000;

    private void SaveShellPreferences()
    {
        if (_previewOnly || Application.Current is not App app) return;
        app.UpdateSettings.MediaOutputIncludePhoneShell =
            IncludePhoneShellBox.IsChecked == true;
        app.UpdateSettings.MediaOutputTransparentShellRecording =
            TransparentShellRecordingBox.IsChecked == true;
        if (TransparentMovQualityBox?.SelectedItem is ComboBoxItem { Tag: string quality } &&
            int.TryParse(quality, out var parsedQuality))
            app.UpdateSettings.MediaOutputTransparentMovQuality = parsedQuality;
        app.UpdateSettings.MediaOutputBackgroundImagePath = _customBackgroundPath;
        app.UpdateSettings.MediaOutputUseDefaultWhiteBackground = _useDefaultWhiteBackground;
        app.UpdateSettings.MediaOutputRecordBitrateAutomatic = _useRecommendedRecordingBitrate;
        if (StreamProtocolBox.SelectedIndex is >= 0 and <= 2)
            app.UpdateSettings.MediaOutputStreamProtocolIndex = StreamProtocolBox.SelectedIndex;
        if (VirtualCameraResolutionBox.SelectedItem is ComboBoxItem { Tag: string cameraSize })
        {
            var dimensions = cameraSize.Split('x');
            if (dimensions.Length == 2 &&
                uint.TryParse(dimensions[0], out var cw) &&
                uint.TryParse(dimensions[1], out var ch) &&
                ValidOutputSize(cw, ch))
            {
                app.UpdateSettings.MediaOutputVirtualCameraWidth = cw;
                app.UpdateSettings.MediaOutputVirtualCameraHeight = ch;
            }
        }
        if (VirtualCameraFpsBox.SelectedItem is ComboBoxItem { Tag: string cameraFps } &&
            int.TryParse(cameraFps, out var cf) && cf is 30 or 60)
            app.UpdateSettings.MediaOutputVirtualCameraFps = cf;
        app.UpdateSettings.MediaOutputRecordingDirectory = RecordingDirectoryBox.Text.Trim();
        if (uint.TryParse(RecordWidthBox.Text, out var rw) &&
            uint.TryParse(RecordHeightBox.Text, out var rh) && ValidOutputSize(rw, rh))
        {
            app.UpdateSettings.MediaOutputRecordWidth = rw;
            app.UpdateSettings.MediaOutputRecordHeight = rh;
            _savedRecordingResolution = true;
        }
        if (uint.TryParse(StreamWidthBox.Text, out var sw) &&
            uint.TryParse(StreamHeightBox.Text, out var sh) && ValidOutputSize(sw, sh))
        {
            app.UpdateSettings.MediaOutputStreamWidth = sw;
            app.UpdateSettings.MediaOutputStreamHeight = sh;
            _savedStreamingResolution = true;
        }
        if (int.TryParse(RecordFpsBox.Text, out var rf) && rf is >= 10 and <= 120)
            app.UpdateSettings.MediaOutputRecordFps = rf;
        if (int.TryParse(StreamFpsBox.Text, out var sf) && sf is >= 10 and <= 60)
            app.UpdateSettings.MediaOutputStreamFps = sf;
        if (int.TryParse(RecordBitrateBox.Text, out var rb) && rb is >= 500 and <= 150000)
            app.UpdateSettings.MediaOutputRecordBitrateKbps = rb;
        if (int.TryParse(StreamBitrateBox.Text, out var sb) && sb is >= 500 and <= 150000)
            app.UpdateSettings.MediaOutputStreamBitrateKbps = sb;
        _ = app.SaveUpdateSettings();
    }

    private void OnShellModeChanged(object sender, RoutedEventArgs e)
    {
        if (_restoringShellPreferences) return;
        if (IncludePhoneShellBox.IsChecked != true &&
            TransparentShellRecordingBox is not null)
            TransparentShellRecordingBox.IsChecked = false;
        RefreshTransparentShellControls();
    }

    private void OnTransparentShellChanged(object sender, RoutedEventArgs e)
    {
        if (_restoringShellPreferences) return;
        if (TransparentShellRecordingBox?.IsChecked == true &&
            IncludePhoneShellBox is not null)
            IncludePhoneShellBox.IsChecked = true;
        RefreshTransparentShellControls();
        if (IsLoaded) SaveShellPreferences();
    }

    private void OnTransparentMovQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_restoringShellPreferences) SaveShellPreferences();
    }

    private void RefreshTransparentShellControls()
    {
        if (TransparentShellRecordingBox is null || BackgroundImageBox is null)
            return;
        var transparent = TransparentShellRecordingBox.IsChecked == true;
        BackgroundImageBox.Text = transparent ? "无背景（真实 Alpha 透明）" :
            _useDefaultWhiteBackground ? "默认白板（9:16）" : _customBackgroundPath;
        if (TransparentMovQualityBox is not null)
            TransparentMovQualityBox.IsEnabled = transparent;
        if (DefaultWhiteBackgroundButton is not null)
            DefaultWhiteBackgroundButton.IsEnabled = !transparent;
        if (ChooseBackgroundButton is not null)
            ChooseBackgroundButton.IsEnabled = !transparent;
        if (EditRecordingLayoutButton is not null)
            EditRecordingLayoutButton.ToolTip = transparent
                ? "可拖动、旋转、按 5% 缩放手机壳；棋盘格仅用于透明预览"
                : "拖动和缩放手机壳";
    }

    private void UpdateRecommendedRecordingBitrate()
    {
        if (!_useRecommendedRecordingBitrate || RecordBitrateBox is null)
            return;
        if (!uint.TryParse(RecordWidthBox?.Text, out var width) ||
            !uint.TryParse(RecordHeightBox?.Text, out var height) ||
            !int.TryParse(RecordFpsBox?.Text, out var fps))
            return;
        var pixels = (ulong)width * height;
        var baseKbps = pixels <= 921600 ? 6000 :
            pixels <= 2073600 ? 12000 : pixels <= 3686400 ? 24000 : 50000;
        var chosen = (int)Math.Clamp(
            Math.Round(baseKbps * Math.Max(1d, fps / 60d) / 500d) * 500d,
            500d, 150000d);
        RecordBitrateBox.Text = chosen.ToString();
    }

    private void OnRecordResolutionPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordWidthBox is null || RecordHeightBox is null ||
            RecordResolutionPresetBox?.SelectedItem is not ComboBoxItem { Tag: string value } ||
            value == "custom") return;
        var dimensions = value.Split('x');
        if (dimensions.Length != 2 ||
            !uint.TryParse(dimensions[0], out var width) ||
            !uint.TryParse(dimensions[1], out var height)) return;
        if (uint.TryParse(RecordWidthBox.Text, out var oldWidth) &&
            uint.TryParse(RecordHeightBox.Text, out var oldHeight) &&
            oldWidth > oldHeight)
            (width, height) = (height, width);
        RecordWidthBox.Text = width.ToString();
        RecordHeightBox.Text = height.ToString();
        UpdateRecommendedRecordingBitrate();
    }

    private void OnRecordFpsPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordFpsBox?.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        RecordFpsBox.Text = value;
        UpdateRecommendedRecordingBitrate();
    }

    private void OnRecordBitratePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordBitrateBox?.SelectedItem is not ComboBoxItem { Tag: string value })
            return;
        _useRecommendedRecordingBitrate = value == "recommend";
        if (_useRecommendedRecordingBitrate) UpdateRecommendedRecordingBitrate();
        else if (int.TryParse(value, out var kbps))
            RecordBitrateBox.Text = kbps.ToString();
    }

    private void OnRecordBitrateEdited(object sender, TextCompositionEventArgs e) =>
        _useRecommendedRecordingBitrate = false;

    private void OnRecordBitrateKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete ||
            e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            _useRecommendedRecordingBitrate = false;
    }

    private string SelectedBackgroundPath =>
        _useDefaultWhiteBackground ? string.Empty : _customBackgroundPath;

    private void OnUseDefaultWhiteBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        _useDefaultWhiteBackground = true;
        BackgroundImageBox.Text = "默认白板（9:16）";
        // Canvas selection is independent of the previously selected resolution.
        SaveShellPreferences();
    }

    private void OnChooseBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        BackgroundImageBox.Text = _customBackgroundPath;
        if (PickImage(BackgroundImageBox))
        {
            _customBackgroundPath = BackgroundImageBox.Text.Trim();
            _useDefaultWhiteBackground = false;
            SaveShellPreferences();
        }
        else
            BackgroundImageBox.Text = _useDefaultWhiteBackground
                ? "默认白板（9:16）" : _customBackgroundPath;
    }
    private void OnChooseRecordingDirectoryClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "选择录制视频的默认保存目录",
            InitialDirectory = Directory.Exists(RecordingDirectoryBox.Text)
                ? RecordingDirectoryBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (picker.ShowDialog(this) == true)
        {
            RecordingDirectoryBox.Text = picker.FolderName;
            SaveShellPreferences();
        }
    }

    private void OnEditRecordingLayoutClick(object sender, RoutedEventArgs e)
    {
        SaveShellPreferences();
        var hasWidth = uint.TryParse(RecordWidthBox.Text, out var width);
        var hasHeight = uint.TryParse(RecordHeightBox.Text, out var height);
        var canvasWidth = hasWidth ? Math.Clamp(width + (width & 1U), 160U, 4096U) : 1080U;
        var canvasHeight = hasHeight ? Math.Clamp(height + (height & 1U), 160U, 4096U) : 1920U;
        var landscape = canvasWidth > canvasHeight;
        var editor = new RecordingLayoutEditorWindow(
            TransparentShellRecordingBox.IsChecked == true
                ? string.Empty : SelectedBackgroundPath,
            landscape, canvasWidth, canvasHeight,
            transparentBackground: TransparentShellRecordingBox.IsChecked == true)
            { Owner = this };
        if (editor.ShowDialog() == true) SaveShellPreferences();
    }


    private bool PickImage(TextBox target)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (File.Exists(target.Text))
            dialog.FileName = target.Text;
        if (dialog.ShowDialog(this) != true) return false;
        target.Text = dialog.FileName;
        return true;
    }

    private void OnStreamProtocolChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateStartButtons();

    private void SelectFirstSupportedProtocol()
    {
        if (SelectedProtocolSupported()) return;
        StreamProtocolBox.SelectedItem = new[]
        {
            RtmpProtocolItem,
            SrtProtocolItem,
            WhipProtocolItem,
        }.FirstOrDefault(item => item.IsEnabled);
    }

    private void UpdateStartButtons()
    {
        if (!IsLoaded) return;
        if (_previewOnly)
        {
            StartRecordingButton.IsEnabled = false;
            StartStreamingButton.IsEnabled = false;
            return;
        }
        StartRecordingButton.IsEnabled = _viewModel.CanStartMediaOutput &&
            _viewModel.CanRecordMediaOutput;
        StartStreamingButton.IsEnabled = _viewModel.CanStartMediaOutput &&
            SelectedProtocolSupported();
    }

    private bool SelectedProtocolSupported() => StreamProtocolBox.SelectedIndex switch
    {
        0 => _viewModel.CanStreamRtmp,
        1 => _viewModel.CanStreamSrt,
        2 => _viewModel.CanStreamWhip,
        _ => false,
    };

    private async void OnStartRecordingClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadOutputSettings(RecordWidthBox, RecordHeightBox, RecordFpsBox,
                RecordBitrateBox, out var width, out var height, out var fps, out var bitrate))
            return;
        SaveShellPreferences();
        StopMediaOutputButton.Content = "完成录制";
        var transparent = TransparentShellRecordingBox.IsChecked == true;
        var movQuality = TransparentMovQualityBox.SelectedItem is ComboBoxItem { Tag: string q } &&
            int.TryParse(q, out var selectedQuality) ? selectedQuality : 18;
        var result = await _viewModel.StartRecordingAsync(
            width, height, fps, bitrate, IncludePhoneShellBox.IsChecked == true,
            transparent ? string.Empty : SelectedBackgroundPath, string.Empty,
            useBackgroundCanvas: !transparent &&
                (_useDefaultWhiteBackground || !string.IsNullOrWhiteSpace(_customBackgroundPath)),
            transparentPhoneShell: transparent, transparentMovQuality: movQuality);
        FeedbackText.Text = result.Message;
    }

    private async void OnStartStreamingClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadOutputSettings(StreamWidthBox, StreamHeightBox, StreamFpsBox,
                StreamBitrateBox, out var width, out var height, out var fps, out var bitrate))
            return;
        var kind = StreamProtocolBox.SelectedIndex switch
        {
            1 => MediaOutputKind.Srt,
            2 => MediaOutputKind.Whip,
            _ => MediaOutputKind.Rtmp,
        };
        SaveShellPreferences();
        StopMediaOutputButton.Content = "停止推流";
        var result = await _viewModel.StartStreamingAsync(kind,
            StreamDestinationBox.Text.Trim(), StreamAuthorizationBox.Text.Trim(),
            width, height, fps, bitrate, IncludePhoneShellBox.IsChecked == true,
            SelectedBackgroundPath, string.Empty,
            useBackgroundCanvas: _useDefaultWhiteBackground ||
                !string.IsNullOrWhiteSpace(_customBackgroundPath));
        FeedbackText.Text = result.Message;
    }

    private async void OnStartVirtualCameraClick(object sender, RoutedEventArgs e)
    {
        if (VirtualCameraResolutionBox.SelectedItem is not ComboBoxItem
                { Tag: string resolution } ||
            VirtualCameraFpsBox.SelectedItem is not ComboBoxItem
                { Tag: string frameRateText } ||
            !TryParseResolution(resolution, out var width, out var height) ||
            !int.TryParse(frameRateText, out var frameRate))
        {
            FeedbackText.Text = LocalizationService.Get("MediaOutputInvalidSettings");
            return;
        }
        var result = await _viewModel.StartVirtualCameraAsync(
            width, height, frameRate);
        FeedbackText.Text = result.Message;
    }

    private static bool TryParseResolution(string value,
        out uint width, out uint height)
    {
        width = height = 0;
        var parts = value.Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && uint.TryParse(parts[0], out width) &&
            uint.TryParse(parts[1], out height);
    }

    private async void OnInstallVirtualCameraClick(object sender, RoutedEventArgs e)
    {
        var result = await _viewModel.InstallVirtualCameraAsync();
        FeedbackText.Text = result.Message;
    }

    private async void OnUninstallVirtualCameraClick(object sender, RoutedEventArgs e)
    {
        var result = await _viewModel.UninstallVirtualCameraAsync();
        FeedbackText.Text = result.Message;
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => _viewModel.StopMediaOutputAsync());
        await PromptToSaveRecordingAsync();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PendingRecordingPath is null) return;
        if (MessageBox.Show(this, LocalizationService.Get("DiscardRecordingConfirmation"),
                LocalizationService.Get("DiscardRecording"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        FeedbackText.Text = _viewModel.DiscardPendingRecording()
            ? LocalizationService.Get("RecordingDiscarded")
            : LocalizationService.Get("RecordingDiscardFailed");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task PromptToSaveRecordingAsync()
    {
        if (_savePromptOpen || _viewModel.IsMediaOutputRunning) return;
        var pending = _viewModel.PendingRecordingPath;
        if (pending is null) return;
        _savePromptOpen = true;
        try
        {
            // Finalize directly into the user's configured folder. The
            // staging file is retained if saving fails.
            var directory = RecordingDirectoryBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.MyVideos), "iPhoneMirror");
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            var extension = string.Equals(Path.GetExtension(pending), ".mov",
                StringComparison.OrdinalIgnoreCase) ? ".mov" : ".mp4";
            var basename = $"iPhoneMirror_{DateTime.Now:yyyyMMdd_HHmmss}";
            var destination = Path.Combine(directory, basename + extension);
            for (var counter = 1; File.Exists(destination); counter++)
                destination = Path.Combine(directory, $"{basename}_{counter}{extension}");
            try { File.Move(pending, destination, overwrite: false); }
            catch (IOException) when (File.Exists(pending) && !File.Exists(destination))
            {
                // Cross-volume save must never create a half-written .mov in
                // the user's output folder. Copy to a unique hidden staging
                // file, flush and atomically rename after successful copying.
                var working = Path.Combine(directory,
                    "." + Path.GetFileName(destination) + ".partial-" +
                    Guid.NewGuid().ToString("N"));
                try
                {
                    await using (var source = new FileStream(pending, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var target = new FileStream(working, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 1024 * 1024,
                        FileOptions.Asynchronous))
                    {
                        await source.CopyToAsync(target);
                        await target.FlushAsync();
                    }
                    File.Move(working, destination, overwrite: false);
                    File.Delete(pending);
                }
                finally
                {
                    // Never delete the completed MOV/MP4. Only remove this
                    // failed transient copy, and leave existing user files.
                    if (File.Exists(working))
                    {
                        try { File.Delete(working); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            var removedSidecars =
                PendingRecordingStore.CleanupSavedRecordingSidecars(destination);
            _viewModel.MarkPendingRecordingSaved(pending);
            if (removedSidecars > 0)
                DiagnosticLogger.Info("recording",
                    "unneeded_destination_mov_sidecars_removed",
                    ("count", removedSidecars),
                    ("saved_file", Path.GetFileName(destination)));
            FeedbackText.Text = LocalizationService.Format(
                "RecordingSavedFormat", destination);
            DiagnosticLogger.Info("recording", "saved_to_default_directory",
                ("file", Path.GetFileName(destination)));
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("recording", "save_failed", error);
            FeedbackText.Text = LocalizationService.Format(
                "RecordingSaveFailedFormat", error.Message);
        }
        finally { _savePromptOpen = false; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("media_output", "settings_action_failed", error);
            FeedbackText.Text = error.Message;
        }
    }

    private static string ReadOutputValue(Control control) => control switch
    {
        ComboBox combo => combo.Text,
        TextBox text => text.Text,
        _ => string.Empty,
    };

    private bool TryReadOutputSettings(TextBox widthBox, TextBox heightBox,
        Control fpsBox, Control bitrateBox, out uint width, out uint height,
        out int fps, out int bitrate)
    {
        width = height = 0;
        fps = bitrate = 0;
        if (uint.TryParse(widthBox.Text, out width) &&
            uint.TryParse(heightBox.Text, out height) &&
            int.TryParse(ReadOutputValue(fpsBox), out fps) &&
            int.TryParse(ReadOutputValue(bitrateBox), out bitrate) &&
            width is >= 160 and <= 4096 && height is >= 160 and <= 4096 &&
            fps >= 10 && fps <= (ReferenceEquals(fpsBox, RecordFpsBox) ? 120 : 60) &&
            bitrate is >= 500 and <= 150000)
        {
            var oldW = width;
            var oldH = height;
            width += width & 1U;
            height += height & 1U;
            if (width != oldW || height != oldH)
            {
                widthBox.Text = width.ToString();
                heightBox.Text = height.ToString();
                FeedbackText.Text = $"编码器要求偶数尺寸，已自动调整为 {width}×{height}。";
            }
            return true;
        }
        FeedbackText.Text = LocalizationService.Get("MediaOutputInvalidSettings");
        return false;
    }
}
