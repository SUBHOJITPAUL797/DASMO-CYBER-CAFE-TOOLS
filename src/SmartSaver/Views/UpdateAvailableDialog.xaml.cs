using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SmartSaver.Services;
using Serilog;

namespace SmartSaver.Views;

public partial class UpdateAvailableDialog : Window
{
    private CancellationTokenSource? _downloadCts;
    private bool _isInstalling = false;
    private readonly UpdateCheckResult _updateInfo;

    public UpdateAvailableDialog(UpdateCheckResult updateInfo)
    {
        InitializeComponent();
        _updateInfo = updateInfo ?? throw new ArgumentNullException(nameof(updateInfo));
        InitializeData();
        Loaded += (s, e) => InitializeData();
    }

    private void InitializeData()
    {
        try
        {
            string currentVer = string.IsNullOrWhiteSpace(_updateInfo.CurrentVersion)
                ? AppUpdateService.CurrentVersion
                : _updateInfo.CurrentVersion;

            TxtCurrentVersion.Text = $"v{currentVer}";
            TxtLatestVersion.Text = $"v{_updateInfo.LatestVersion}";

            if (!string.IsNullOrWhiteSpace(_updateInfo.CustomAdminMessage))
            {
                TxtAdminMessage.Text = _updateInfo.CustomAdminMessage;
                BoxAdminMessage.Visibility = Visibility.Visible;
            }
            else
            {
                BoxAdminMessage.Visibility = Visibility.Collapsed;
            }

            TxtChangelog.Text = AppUpdateService.FormatReleaseHighlights(_updateInfo.ReleaseNotes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize UpdateAvailableDialog data");
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null)
        {
            _downloadCts.Cancel();
        }
        DialogResult = false;
        Close();
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        string downloadUrl = _updateInfo.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            System.Windows.MessageBox.Show(
                "Direct download link is currently being prepared on the cloud server. Please visit GitHub or contact developer support.",
                "Download Link Pending", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnDownloadUpdate.IsEnabled = false;
        BtnDownloadUpdate.Content = "⏳ Downloading...";
        BtnRemindLater.Visibility = Visibility.Collapsed;
        BtnCancelDownload.Visibility = Visibility.Visible;
        PanelProgress.Visibility = Visibility.Visible;

        _downloadCts = new CancellationTokenSource();

        try
        {
            var targetPath = await AppUpdateService.Instance.DownloadUpdateAsync(
                downloadUrl,
                progress =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ProgressBarDownload.Value = progress.Percentage;
                        TxtProgressStatus.Text = progress.StatusMessage;
                        TxtProgressStats.Text = $"{progress.FormattedDownloaded} / {progress.FormattedTotal} • {progress.FormattedSpeed}";
                    });
                },
                _downloadCts.Token);

            if (targetPath != null)
            {
                _isInstalling = true;
                Dispatcher.Invoke(() =>
                {
                    TxtProgressStatus.Text = "✅ Download Complete! Starting installer...";
                    BtnDownloadUpdate.Content = "🚀 Launching Installer...";
                });

                await Task.Delay(500);
                AppUpdateService.Instance.LaunchInstallerAndExit(targetPath);
            }
        }
        catch (OperationCanceledException)
        {
            TxtProgressStatus.Text = "Download canceled.";
            ResetButtons();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed downloading update in UpdateAvailableDialog");
            System.Windows.MessageBox.Show($"Failed to download update: {ex.Message}\n\nPlease check your internet connection.",
                "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ResetButtons();
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private void ResetButtons()
    {
        BtnDownloadUpdate.IsEnabled = true;
        BtnDownloadUpdate.Content = "🚀 Download & Install Update Now";
        BtnRemindLater.Visibility = Visibility.Visible;
        BtnCancelDownload.Visibility = Visibility.Collapsed;
        PanelProgress.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_isInstalling)
        {
            _downloadCts?.Cancel();
        }
    }
}
