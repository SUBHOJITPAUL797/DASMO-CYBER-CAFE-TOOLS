using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SmartSaver.Services;
using Serilog;

namespace SmartSaver.Views;

public partial class MandatoryUpdateWindow : Window
{
    private CancellationTokenSource? _downloadCts;
    private bool _isInstalling = false;
    private UpdateCheckResult? _updateInfo;

    public MandatoryUpdateWindow(UpdateCheckResult? updateInfo = null)
    {
        InitializeComponent();
        _updateInfo = updateInfo;
        Loaded += async (s, e) => await InitializeDataAsync();
    }

    private async Task InitializeDataAsync()
    {
        try
        {
            if (_updateInfo == null)
            {
                _updateInfo = await AppUpdateService.Instance.CheckForUpdateAsync();
            }

            TxtCurrentVersion.Text = $"v{AppUpdateService.CurrentVersion} (Retired)";
            TxtRequiredVersion.Text = $"v{_updateInfo.LatestVersion}+";

            if (!string.IsNullOrWhiteSpace(_updateInfo.CustomAdminMessage))
            {
                TxtAdminMessage.Text = _updateInfo.CustomAdminMessage;
                BoxAdminMessage.Visibility = Visibility.Visible;
            }
            else
            {
                BoxAdminMessage.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(_updateInfo.ReleaseNotes))
            {
                TxtChangelog.Text = _updateInfo.ReleaseNotes;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize MandatoryUpdateWindow data");
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown(0);
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInfo == null)
        {
            _updateInfo = await AppUpdateService.Instance.CheckForUpdateAsync();
        }

        string downloadUrl = _updateInfo.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            System.Windows.MessageBox.Show("Direct download link is currently being prepared on the cloud server. Please contact Subhojit Paul (+91 8927408840) to receive the latest installer.",
                "Download Link Pending", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnDownloadUpdate.IsEnabled = false;
        BtnDownloadUpdate.Content = "⏳ Downloading...";
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
            Log.Error(ex, "Failed downloading update in MandatoryUpdateWindow");
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
        BtnCancelDownload.Visibility = Visibility.Collapsed;
        PanelProgress.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_isInstalling && !FirebaseCloudAuthService.BypassForTests)
        {
            System.Windows.Application.Current.Shutdown(0);
        }
    }
}
