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
                p.MediaOutputShellCenterY, p.MediaOutputShellScale);
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
        BackgroundImageBox.Text = settings.MediaOutputBackgroundImagePath;
        ShellOptionsPanel.IsEnabled = IncludePhoneShellBox.IsChecked == true;
    }

    private void SaveShellPreferences()
    {
        if (_previewOnly || Application.Current is not App app) return;
        app.UpdateSettings.MediaOutputIncludePhoneShell =
            IncludePhoneShellBox.IsChecked == true;
        app.UpdateSettings.MediaOutputBackgroundImagePath =
            BackgroundImageBox.Text.Trim();
        app.UpdateSettings.MediaOutputPhoneShellImagePath = string.Empty;
        app.UpdateSettings.MediaOutputRecordingDirectory = RecordingDirectoryBox.Text.Trim();
        _ = app.SaveUpdateSettings();
    }

    private void OnShellModeChanged(object sender, RoutedEventArgs e)
    {
        if (ShellOptionsPanel is not null)
            ShellOptionsPanel.IsEnabled = IncludePhoneShellBox.IsChecked == true;
    }

    private void OnChooseBackgroundClick(object sender, RoutedEventArgs e) =>
        PickImage(BackgroundImageBox);
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

    private void OnImportGlobalShellClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入全局 iPhone 16 Pro Max 手机壳",
            Filter = "PNG 手机壳 (*.png)|*.png",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            PhoneShellAssetStore.Install(dialog.FileName);
            DiagnosticLogger.Info("media_output", "global_shell_installed",
                ("file", Path.GetFileName(PhoneShellAssetStore.CustomPath)));
            MessageBox.Show(this,
                "新手机壳已安装，录制、推流和布局编辑器将使用它。" +
                " 为让已经创建的独立窗口重新载入 GPU 贴图，请重启软件。",
                "手机壳更新完成", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("media_output", "global_shell_import_failed", error);
            MessageBox.Show(this, error.Message, "导入手机壳失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnEditRecordingLayoutClick(object sender, RoutedEventArgs e)
    {
        SaveShellPreferences();
        var hasWidth = uint.TryParse(RecordWidthBox.Text, out var width);
        var hasHeight = uint.TryParse(RecordHeightBox.Text, out var height);
        var canvasWidth = hasWidth ? Math.Clamp(width & ~1U, 160U, 3840U) : 1280U;
        var canvasHeight = hasHeight ? Math.Clamp(height & ~1U, 160U, 2160U) : 720U;
        var landscape = canvasWidth > canvasHeight;
        var editor = new RecordingLayoutEditorWindow(
            BackgroundImageBox.Text.Trim(), landscape, canvasWidth, canvasHeight)
            { Owner = this };
        if (editor.ShowDialog() == true) SaveShellPreferences();
    }


    private void PickImage(TextBox target)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (File.Exists(target.Text))
            dialog.FileName = target.Text;
        if (dialog.ShowDialog(this) != true) return;
        target.Text = dialog.FileName;
        SaveShellPreferences();
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
            BackgroundImageBox.Text.Trim(), string.Empty);
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
            BackgroundImageBox.Text.Trim(), string.Empty);
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
            width >= 160 && height >= 160 && fps is >= 10 and <= 60 &&
            bitrate is >= 500 and <= 50000)
            return true;
        FeedbackText.Text = LocalizationService.Get("MediaOutputInvalidSettings");
        return false;
    }
}
