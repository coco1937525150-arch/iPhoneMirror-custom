using System.Diagnostics;
using System.IO;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public partial class StartupErrorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _logPath;

    internal StartupErrorWindow(Exception error, string logPath)
    {
        _logPath = logPath;
        InitializeComponent();
        try { ThemeService.Attach(this); }
        catch (Exception themeError)
        {
            DiagnosticLogger.Exception("startup", "error_window_theme_failed",
                themeError);
        }

        var language = LocalizationService.StartupCultureName;
        var hongKong = language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-Hant-HK", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-MO", StringComparison.OrdinalIgnoreCase);
        var chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        HeadingText.Text = LocalizationService.Get("StartupErrorHeading");
        SummaryText.Text = StartupDiagnostics.UserMessage(error,
            hongKong ? "zh-HK" : chinese ? "zh-CN" : "en-US");
        LogLabelText.Text = LocalizationService.Get("StartupErrorLogLabel");
        LogPathTextBox.Text = logPath;
        DetailsExpander.Header = LocalizationService.Get("StartupErrorDetails");
        DetailsTextBox.Text = error.ToString();
        OpenLogButton.Content = LocalizationService.Get("StartupErrorOpenLog");
        CloseButton.Content = LocalizationService.Get("StartupErrorClose");
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var arguments = File.Exists(_logPath)
                ? $"/select,\"{_logPath}\""
                : $"\"{directory}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("startup", "open_log_location_failed", error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
