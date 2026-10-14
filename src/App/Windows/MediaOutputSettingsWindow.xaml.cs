using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private (uint Width, uint Height)? _lastDefaultSize;
    private string _customBackgroundPath = string.Empty;
    private bool _useDefaultWhiteBackground;

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
        PhoneShellLayoutSettingsProvider.Current = () =>
        {
            if (Application.Current is not App app) return null;
            var p = app.UpdateSettings;
            return new PhoneShellLayoutSettings(p.MediaOutputShellCenterX,
                p.MediaOutputShellCenterY, p.MediaOutputShellScale,
                p.MediaOutputShellRotationDegrees);
        };
        RecordFpsBox.Text = StreamFpsBox.Text = "60";
        if (Application.Current is App settingsApp)
            RecordingDirectoryBox.Text = settingsApp.UpdateSettings.MediaOutputRecordingDirectory;
        RecordBitrateBox.Text = StreamBitrateBox.Text = "6000";
        VirtualCameraFpsBox.SelectedIndex = 0;
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
        var updateRecording = force || previous is null ||
            (RecordWidthBox.Text == previous.Value.Width.ToString() &&
             RecordHeightBox.Text == previous.Value.Height.ToString());
        var updateStreaming = force || previous is null ||
            (StreamWidthBox.Text == previous.Value.Width.ToString() &&
             StreamHeightBox.Text == previous.Value.Height.ToString());
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
        IncludePhoneShellBox.IsChecked = settings.MediaOutputIncludePhoneShell;
        _customBackgroundPath = settings.MediaOutputBackgroundImagePath ?? string.Empty;
        _useDefaultWhiteBackground = settings.MediaOutputUseDefaultWhiteBackground ??
            string.IsNullOrWhiteSpace(_customBackgroundPath);
        BackgroundImageBox.Text = _useDefaultWhiteBackground
            ? "默认白板（9:16）" : _customBackgroundPath;
        if (_useDefaultWhiteBackground)
        {
            RecordWidthBox.Text = "1080";
            RecordHeightBox.Text = "1920";
            if (RecordResolutionPresetBox is not null)
                RecordResolutionPresetBox.SelectedIndex = 2;
        }
        ShellOptionsPanel.IsEnabled = IncludePhoneShellBox.IsChecked == true;
    }

    private void SaveShellPreferences()
    {
        if (_previewOnly || Application.Current is not App app) return;
        app.UpdateSettings.MediaOutputIncludePhoneShell =
            IncludePhoneShellBox.IsChecked == true;
        app.UpdateSettings.MediaOutputBackgroundImagePath = _customBackgroundPath;
        app.UpdateSettings.MediaOutputUseDefaultWhiteBackground = _useDefaultWhiteBackground;
        app.UpdateSettings.MediaOutputRecordingDirectory = RecordingDirectoryBox.Text.Trim();
        _ = app.SaveUpdateSettings();
    }

    private void OnShellModeChanged(object sender, RoutedEventArgs e)
    {
        if (ShellOptionsPanel is not null)
            ShellOptionsPanel.IsEnabled = IncludePhoneShellBox.IsChecked == true;
    }

    private void UpdateRecommendedRecordingBitrate()
    {
        if (RecordBitratePresetBox?.SelectedItem is not ComboBoxItem { Tag: "recommend" })
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
        if (RecordFpsBox is null || RecordFpsPresetBox?.SelectedItem
            is not ComboBoxItem { Tag: string value } || value == "custom") return;
        RecordFpsBox.Text = value;
        UpdateRecommendedRecordingBitrate();
    }

    private void OnRecordBitratePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordBitrateBox is null ||
            RecordBitratePresetBox?.SelectedItem is not ComboBoxItem { Tag: string value })
            return;
        if (value == "recommend") UpdateRecommendedRecordingBitrate();
        else if (int.TryParse(value, out var kbps))
            RecordBitrateBox.Text = kbps.ToString();
    }

    private string SelectedBackgroundPath =>
        _useDefaultWhiteBackground ? string.Empty : _customBackgroundPath;

    private void OnUseDefaultWhiteBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        _useDefaultWhiteBackground = true;
        BackgroundImageBox.Text = "默认白板（9:16）";
        RecordWidthBox.Text = "1080";
        RecordHeightBox.Text = "1920";
        RecordResolutionPresetBox.SelectedIndex = 2;
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
            SelectedBackgroundPath, landscape, canvasWidth, canvasHeight)
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
        var result = await _viewModel.StartRecordingAsync(
            width, height, fps, bitrate, IncludePhoneShellBox.IsChecked == true,
            SelectedBackgroundPath, string.Empty);
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
            SelectedBackgroundPath, string.Empty);
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
            var filename = $"iPhoneMirror_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var destination = Path.Combine(directory, filename);
            for (var counter = 1; File.Exists(destination); counter++)
                destination = Path.Combine(directory,
                    $"iPhoneMirror_{DateTime.Now:yyyyMMdd_HHmmss}_{counter}.mp4");
            try { File.Move(pending, destination, overwrite: false); }
            catch (IOException)
            {
                // Also works when the temporary staging volume is not the
                // same physical disk as the user's chosen video folder.
                await using var source = new FileStream(pending, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var target = new FileStream(destination, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1024 * 1024,
                    FileOptions.Asynchronous);
                await source.CopyToAsync(target);
                await target.FlushAsync();
                File.Delete(pending);
            }
            _viewModel.MarkPendingRecordingSaved(pending);
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

    private bool TryReadOutputSettings(TextBox widthBox, TextBox heightBox,
        TextBox fpsBox, TextBox bitrateBox, out uint width, out uint height,
        out int fps, out int bitrate)
    {
        width = height = 0;
        fps = bitrate = 0;
        if (uint.TryParse(widthBox.Text, out width) &&
            uint.TryParse(heightBox.Text, out height) &&
            int.TryParse(fpsBox.Text, out fps) &&
            int.TryParse(bitrateBox.Text, out bitrate) &&
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
