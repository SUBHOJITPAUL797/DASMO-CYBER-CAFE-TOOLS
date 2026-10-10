using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using System.IO;
using System.Diagnostics;
using SmartSaver.Services;
using Serilog;

namespace SmartSaver.Views;

public class UserRowViewModel
{
    public CloudUserAccount RawUser { get; }

    public UserRowViewModel(CloudUserAccount user)
    {
        RawUser = user;
    }

    public string Name => string.IsNullOrWhiteSpace(RawUser.Name) ? "—" : RawUser.Name;
    public string Email => RawUser.Email;
    public string DeviceModel => string.IsNullOrEmpty(RawUser.DeviceModel) ? "No Device Bound" : RawUser.DeviceModel;
    public string DeviceId => string.IsNullOrEmpty(RawUser.DeviceId) ? "Unbound" : RawUser.DeviceId;
    public string StatusText => (RawUser.Status ?? "pending").ToUpperInvariant();

    public Brush StatusBg
    {
        get
        {
            string s = (RawUser.Status ?? "").ToLowerInvariant();
            if (s == "approved" || RawUser.IsApproved)
                return new SolidColorBrush(Color.FromRgb(5, 150, 105)); // Green
            if (s == "banned")
                return new SolidColorBrush(Color.FromRgb(220, 38, 38)); // Red
            return new SolidColorBrush(Color.FromRgb(217, 119, 6)); // Amber
        }
    }

    public Brush StatusFg => Brushes.White;

    public string ExpiryDisplay
    {
        get
        {
            if (RawUser.ExpiryTimestamp <= 0) return "Lifetime";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(RawUser.ExpiryTimestamp).ToLocalTime().ToString("dd MMM yyyy");
            }
            catch
            {
                return "Lifetime";
            }
        }
    }

    public string AppVersionDisplay => string.IsNullOrWhiteSpace(RawUser.AppVersion) ? "v1.0 (Old)" : $"v{RawUser.AppVersion}";

    public Brush VersionBg
    {
        get
        {
            string v = RawUser.AppVersion ?? "";
            if (string.IsNullOrEmpty(v)) return new SolidColorBrush(Color.FromRgb(220, 38, 38)); // Red
            if (FirebaseCloudAuthService.IsVersionOutdated(v, FirebaseCloudAuthService.CurrentAppVersion))
                return new SolidColorBrush(Color.FromRgb(217, 119, 6)); // Amber
            return new SolidColorBrush(Color.FromRgb(5, 150, 105)); // Green
        }
    }

    public Brush VersionFg => Brushes.White;
}

public partial class AdminPanelWindow : Window
{
    private string _activeCollection = FirebaseCloudAuthService.PcUsersCollection;
    private List<UserRowViewModel> _allLoaded = new();
    private bool _requireApprovalForPc = true;
    private UserRowViewModel? _selectedSpecsUser;
    private string? _selectedMsiPath;
    private bool _isPublishing = false;

    public AdminPanelWindow()
    {
        InitializeComponent();
        if (!FirebaseCloudAuthService.BypassForTests && !FirebaseCloudAuthService.Instance.IsSuperAdmin)
        {
            System.Windows.MessageBox.Show("Access Denied: Super Admin privileges required.", "DASMO Admin Security", MessageBoxButton.OK, MessageBoxImage.Stop);
            Close();
            return;
        }
        _requireApprovalForPc = FirebaseCloudAuthService.Instance.GetLocalGateCache();
        UpdateGateUi();
        Loaded += async (s, e) => await RefreshDataAsync();
        StateChanged += (s, e) => UpdateMaximizeButtonVisual(WindowState == WindowState.Maximized);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2)
            {
                Maximize_Click(sender, e);
            }
            else
            {
                DragMove();
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
        UpdateMaximizeButtonVisual(WindowState == WindowState.Maximized);
    }

    private void UpdateMaximizeButtonVisual(bool isMaximized)
    {
        if (IconMax != null && IconRestore != null && BtnMaximize != null)
        {
            IconMax.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
            IconRestore.Visibility = isMaximized ? Visibility.Visible : Visibility.Collapsed;
            BtnMaximize.ToolTip = isMaximized ? "Restore Down" : "Maximize";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void CollectionTab_Checked(object sender, RoutedEventArgs e)
    {
        if (TabPc?.IsChecked == true)
            _activeCollection = "dasmo_pc_users";
        else if (TabCapture?.IsChecked == true)
            _activeCollection = "dasmo_cyber_capture_users";
        else if (TabScanner?.IsChecked == true)
            _activeCollection = "dasmo_scanner_users";
        else if (TabPhoto?.IsChecked == true)
            _activeCollection = "dasmo_photo_print_users";

        await RefreshDataAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync();
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;

            // 1. Fetch licensing registration gate status
            try
            {
                _requireApprovalForPc = await FirebaseCloudAuthService.Instance.IsApprovalRequiredForPcAsync();
                UpdateGateUi();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to read licensing gate");
            }

            // 2. Fetch users in current collection
            var users = await FirebaseCloudAuthService.Instance.GetAllUsersAsync(_activeCollection);
            _allLoaded = users.Select(u => new UserRowViewModel(u)).ToList();
            ApplyFilterAndRender();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh users in AdminPanel");
            System.Windows.MessageBox.Show("Failed to fetch cloud users: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void UpdateGateUi()
    {
        if (TxtGateMode == null || BadgeGateMode == null || BtnToggleGate == null) return;

        if (_requireApprovalForPc)
        {
            TxtGateMode.Text = "🔒 GATED (Approval Required)";
            TxtGateMode.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
            BadgeGateMode.Background = new SolidColorBrush(Color.FromRgb(43, 33, 20));
            BtnToggleGate.Content = "⚡ Switch to Open Mode";
            BtnToggleGate.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // Blue
        }
        else
        {
            TxtGateMode.Text = "⚡ OPEN (Instant Direct Login)";
            TxtGateMode.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
            BadgeGateMode.Background = new SolidColorBrush(Color.FromRgb(19, 42, 34));
            BtnToggleGate.Content = "🔒 Switch to Gated Mode";
            BtnToggleGate.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6)); // Amber
        }
    }

    private async void ToggleGate_Click(object sender, RoutedEventArgs e)
    {
        string targetMode = _requireApprovalForPc ? "OPEN MODE (Instant Direct Entry without admin approval)" : "GATED MODE (Admin Approval Required for all new registrations)";
        var res = System.Windows.MessageBox.Show(
            $"Change Registration Gate to:\n\n{targetMode}?\n\nIn Open Mode, any user who inputs their Name & Email can immediately enter the software without waiting for you to click Approve.",
            "Confirm Mode Switch", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                bool newSetting = !_requireApprovalForPc;
                bool ok = await FirebaseCloudAuthService.Instance.SetApprovalRequiredForPcAsync(newSetting);
                if (ok)
                {
                    _requireApprovalForPc = newSetting;
                    UpdateGateUi();
                    System.Windows.MessageBox.Show($"Registration Gate successfully changed to:\n\n{(_requireApprovalForPc ? "🔒 GATED (Admin Approval Required)" : "⚡ OPEN (Instant Direct Login)")}", "Gate Mode Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show("Failed to update registration gate. Please check internet connection.", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    #region Pre-Approval Modal Logic

    private void OpenPreApprove_Click(object sender, RoutedEventArgs e)
    {
        TxtPreApproveName.Text = string.Empty;
        TxtPreApproveEmail.Text = string.Empty;
        OverlayPreApprove.Visibility = Visibility.Visible;
        TxtPreApproveName.Focus();
    }

    private void CancelPreApprove_Click(object sender, RoutedEventArgs e)
    {
        OverlayPreApprove.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmPreApprove_Click(object sender, RoutedEventArgs e)
    {
        string name = TxtPreApproveName.Text.Trim();
        string email = TxtPreApproveEmail.Text.Trim();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            System.Windows.MessageBox.Show("Please enter a valid email address.", "Invalid Email", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtPreApproveEmail.Focus();
            return;
        }

        OverlayPreApprove.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            bool ok = await FirebaseCloudAuthService.Instance.PreApproveUserAsync(_activeCollection, email, name);
            if (ok)
            {
                await RefreshDataAsync();
                System.Windows.MessageBox.Show(
                    $"User '{email}' pre-approved successfully!\n\nWhen they launch the software and enter this email, their physical PC hardware will bind automatically and they will enter instantly without any waiting screen.",
                    "Pre-Approved Successfully", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show($"Failed to pre-approve '{email}'. Please check internet connection.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    #endregion

    #region Version Policy & 1-Click Release Publisher Logic

    private async void OpenVersionPolicy_Click(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var policy = await FirebaseCloudAuthService.Instance.GetVersionPolicyAsync();
            TxtPolicyLatestVersion.Text = string.IsNullOrWhiteSpace(policy.LatestVersion) ? FirebaseCloudAuthService.CurrentAppVersion : policy.LatestVersion;
            TxtPolicyMinVersion.Text = string.IsNullOrWhiteSpace(policy.MinRequiredVersion) ? FirebaseCloudAuthService.CurrentAppVersion : policy.MinRequiredVersion;
            TxtPolicyBlockedVersions.Text = policy.BlockedVersions ?? string.Empty;
            ChkPolicyForceUpdate.IsChecked = policy.ForceUpdate;
            TxtPolicyCustomMessage.Text = policy.CustomUpdateMessage ?? string.Empty;
            TxtPolicyGithubRepo.Text = string.IsNullOrWhiteSpace(policy.GithubRepo) ? AppUpdateService.DefaultGithubRepo : policy.GithubRepo;
            TxtPolicyDownloadUrl.Text = policy.UpdateDownloadUrl ?? string.Empty;
            TxtPolicyChangelog.Text = policy.UpdateChangelog ?? string.Empty;

            // Populate 1-Click Release Publisher inputs
            TxtPublisherVersion.Text = string.IsNullOrWhiteSpace(policy.LatestVersion) ? FirebaseCloudAuthService.CurrentAppVersion : policy.LatestVersion;
            TxtPublisherTitle.Text = $"DASMO Cyber Cafe Tools v{TxtPublisherVersion.Text} Official Release";
            TxtPublisherChangelog.Text = string.IsNullOrWhiteSpace(policy.UpdateChangelog)
                ? "- Performance and stability enhancements\n- Document Stacker & Passport Photo Studio improvements\n- Cloud licensing & auto-update updates"
                : policy.UpdateChangelog;
            TxtPublisherMinVersion.Text = string.IsNullOrWhiteSpace(policy.MinRequiredVersion) ? "1.5.0" : policy.MinRequiredVersion;
            TxtPublisherCustomNotice.Text = policy.CustomUpdateMessage ?? string.Empty;
            ChkPublisherForceUpdate.IsChecked = policy.ForceUpdate;

            // Auto-detect built MSI if not already selected
            if (string.IsNullOrEmpty(_selectedMsiPath) || !File.Exists(_selectedMsiPath))
            {
                string? detectedMsi = FindBuiltMsiPath();
                if (!string.IsNullOrEmpty(detectedMsi))
                {
                    UpdateSelectedMsi(detectedMsi);
                }
            }

            OverlayVersionPolicy.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load version policy");
            System.Windows.MessageBox.Show($"Failed to load version policy: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void CloseVersionPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_isPublishing)
        {
            System.Windows.MessageBox.Show("Release publishing is in progress. Please wait until publication finishes.", "Publishing in Progress", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OverlayVersionPolicy.Visibility = Visibility.Collapsed;
    }

    private void VersionSubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelReleasePublisher == null || PanelManualPolicy == null) return;

        if (TabReleasePublisher.IsChecked == true)
        {
            PanelReleasePublisher.Visibility = Visibility.Visible;
            PanelManualPolicy.Visibility = Visibility.Collapsed;
            TabReleasePublisher.FontWeight = FontWeights.Bold;
            TabReleasePublisher.Foreground = Brushes.White;
            TabManualPolicy.FontWeight = FontWeights.SemiBold;
            TabManualPolicy.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225));
        }
        else
        {
            PanelReleasePublisher.Visibility = Visibility.Collapsed;
            PanelManualPolicy.Visibility = Visibility.Visible;
            TabManualPolicy.FontWeight = FontWeights.Bold;
            TabManualPolicy.Foreground = Brushes.White;
            TabReleasePublisher.FontWeight = FontWeights.SemiBold;
            TabReleasePublisher.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225));
        }
    }

    private void UpdateSelectedMsi(string path)
    {
        if (!File.Exists(path)) return;
        _selectedMsiPath = path;
        var fi = new FileInfo(path);
        double mb = fi.Length / (1024.0 * 1024.0);
        TxtSelectedMsiFileName.Text = $"📦 {fi.Name}";
        TxtSelectedMsiFileSize.Text = $"{mb:F2} MB • {fi.FullName}";
        TxtSelectedMsiFileName.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    }

    private void BrowseMsi_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Compiled DASMO Setup Installer (.msi)",
            Filter = "Windows Installer Package (*.msi)|*.msi|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
        {
            UpdateSelectedMsi(dlg.FileName);
            AppendPublishLog($"Selected installer package: {dlg.FileName}");
        }
    }

    private void AutoDetectMsi_Click(object sender, RoutedEventArgs e)
    {
        string? found = FindBuiltMsiPath();
        if (!string.IsNullOrEmpty(found))
        {
            UpdateSelectedMsi(found);
            AppendPublishLog($"[Auto-Detect] Located compiled installer:\n{found}");
        }
        else
        {
            System.Windows.MessageBox.Show("Could not find 'DASMO_CYBER_CAFE_TOOLS_Setup.msi' automatically.\n\nPlease click 'Browse .msi...' to select it manually.", "MSI Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void MsiDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files != null && files.Length > 0 && files[0].EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void MsiDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                var msi = files.FirstOrDefault(f => f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(msi) && File.Exists(msi))
                {
                    UpdateSelectedMsi(msi);
                    AppendPublishLog($"Dropped installer package: {msi}");
                }
                else
                {
                    System.Windows.MessageBox.Show("Please drop a valid Windows Installer (.msi) package.", "Invalid File", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private string? FindBuiltMsiPath()
    {
        string[] candidates = new[]
        {
            @"C:\CODING\coading\DASMO CYBER CAFE\src\SmartSaver.Installer\bin\Release\DASMO_CYBER_CAFE_TOOLS_Setup.msi",
            @"C:\CODING\coading\DASMO CYBER CAFE\releases\DASMO_CYBER_CAFE_TOOLS_Setup.msi",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\SmartSaver.Installer\bin\Release\DASMO_CYBER_CAFE_TOOLS_Setup.msi"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\SmartSaver.Installer\bin\Release\DASMO_CYBER_CAFE_TOOLS_Setup.msi"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DASMO_CYBER_CAFE_TOOLS_Setup.msi")
        };

        foreach (var cand in candidates)
        {
            try
            {
                string full = Path.GetFullPath(cand);
                if (File.Exists(full)) return full;
            }
            catch { }
        }

        try
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 5 && dir != null; dir = dir.Parent, i++)
            {
                string testPath = Path.Combine(dir.FullName, @"src\SmartSaver.Installer\bin\Release\DASMO_CYBER_CAFE_TOOLS_Setup.msi");
                if (File.Exists(testPath)) return testPath;
            }
        }
        catch { }

        return null;
    }

    private void AppendPublishLog(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            if (string.IsNullOrEmpty(TxtPublishLogs.Text) || TxtPublishLogs.Text.StartsWith("Ready to publish"))
                TxtPublishLogs.Text = $"[{time}] {msg}";
            else
                TxtPublishLogs.Text += $"\n[{time}] {msg}";

            ScrollPublishLogs.ScrollToEnd();
        });
    }

    private string? FindGhCliPath()
    {
        string defaultPath = @"C:\Program Files\GitHub CLI\gh.exe";
        if (File.Exists(defaultPath)) return defaultPath;

        string localAppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\GitHub CLI\gh.exe");
        if (File.Exists(localAppDataPath)) return localAppDataPath;

        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string cand = Path.Combine(progFiles, @"GitHub CLI\gh.exe");
        if (File.Exists(cand)) return cand;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string full = Path.Combine(dir.Trim(), "gh.exe");
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return "gh";
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCliProcessAsync(string exePath, string arguments, Action<string>? onOutputLine = null)
    {
        var stdoutSb = new System.Text.StringBuilder();
        var stderrSb = new System.Text.StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                stdoutSb.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                stderrSb.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    private async void PublishRelease_Click(object sender, RoutedEventArgs e)
    {
        if (!FirebaseCloudAuthService.BypassForTests && !FirebaseCloudAuthService.Instance.IsSuperAdmin)
        {
            System.Windows.MessageBox.Show("Security Violation: Only Super Admin is authorized to publish releases.", "Security Error", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        if (_isPublishing)
        {
            System.Windows.MessageBox.Show("A release publishing process is already in progress. Please wait for it to complete.", "Operation In Progress", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(_selectedMsiPath) || !File.Exists(_selectedMsiPath))
        {
            System.Windows.MessageBox.Show("Please select or drop a compiled .msi installer package first.", "MSI Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string rawVersion = TxtPublisherVersion.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            System.Windows.MessageBox.Show("Please enter the release version (e.g., 1.5.50.0).", "Version Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtPublisherVersion.Focus();
            return;
        }

        string tag = rawVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? rawVersion : $"v{rawVersion}";
        string cleanVersion = rawVersion.TrimStart('v', 'V');
        string title = string.IsNullOrWhiteSpace(TxtPublisherTitle.Text) ? $"DASMO Cyber Cafe Tools {tag}" : TxtPublisherTitle.Text.Trim();
        string changelog = TxtPublisherChangelog.Text.Trim();
        string repo = string.IsNullOrWhiteSpace(TxtPolicyGithubRepo.Text) ? AppUpdateService.DefaultGithubRepo : TxtPolicyGithubRepo.Text.Trim();
        string minVersion = string.IsNullOrWhiteSpace(TxtPublisherMinVersion.Text) ? cleanVersion : TxtPublisherMinVersion.Text.Trim();
        bool forceUpdate = ChkPublisherForceUpdate.IsChecked == true;
        string customNotice = TxtPublisherCustomNotice.Text.Trim();
        bool syncPolicy = ChkPublisherSyncPolicy.IsChecked == true;

        string? ghPath = FindGhCliPath();
        if (string.IsNullOrEmpty(ghPath))
        {
            System.Windows.MessageBox.Show("GitHub CLI ('gh.exe') could not be found.\n\nPlease ensure GitHub CLI is installed at 'C:\\Program Files\\GitHub CLI\\gh.exe'.", "GitHub CLI Missing", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var msiInfo = new FileInfo(_selectedMsiPath);
        double mb = msiInfo.Length / (1024.0 * 1024.0);

        var confirm = System.Windows.MessageBox.Show(
            $"Are you ready to publish this release to GitHub and broadcast to all users?\n\n" +
            $"• Release Tag:       {tag}\n" +
            $"• Release Title:     {title}\n" +
            $"• MSI Package:       {msiInfo.Name} ({mb:F2} MB)\n" +
            $"• Repository:        {repo}\n" +
            $"• Sync Cloud Policy: {(syncPolicy ? "YES" : "NO")}\n" +
            $"• Force Lockout:     {(forceUpdate ? "YES" : "NO")}\n\n" +
            "Click YES to begin the 1-Click publish operation.",
            "Confirm Release Publication",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _isPublishing = true;
        BtnPublishRelease.IsEnabled = false;
        ProgressBarPublish.Visibility = Visibility.Visible;
        TxtPublishLogs.Text = string.Empty;

        string notesTempFile = Path.Combine(Path.GetTempPath(), $"dasmo_release_notes_{Guid.NewGuid():N}.txt");

        try
        {
            AppendPublishLog($"══════════════════════════════════════════════════════════════");
            AppendPublishLog($"🚀 STARTING 1-CLICK RELEASE PUBLISHER: {tag}");
            AppendPublishLog($"══════════════════════════════════════════════════════════════");
            AppendPublishLog($"Installer Package: {_selectedMsiPath} ({mb:F2} MB)");
            AppendPublishLog($"Target Repository: {repo}");

            // Write notes file
            await File.WriteAllTextAsync(notesTempFile, changelog);

            // Step 1: Check GitHub CLI authentication
            AppendPublishLog("Checking GitHub CLI authentication status...");
            var authCheck = await RunCliProcessAsync(ghPath, "auth status", line => AppendPublishLog($"[gh auth] {line}"));
            if (authCheck.ExitCode != 0 && (authCheck.StdOut + authCheck.StdErr).Contains("not logged into"))
            {
                throw new InvalidOperationException("GitHub CLI is not authenticated. Please run 'gh auth login' in terminal.");
            }

            // Step 2: Publish release via GitHub CLI
            AppendPublishLog($"Publishing release {tag} with installer asset to GitHub...");
            string createArgs = $"release create {tag} \"{_selectedMsiPath}\" --title \"{title}\" -F \"{notesTempFile}\" --latest -R \"{repo}\"";
            AppendPublishLog($"Running: gh {createArgs}");

            var createRes = await RunCliProcessAsync(ghPath, createArgs, line => AppendPublishLog(line));

            if (createRes.ExitCode != 0)
            {
                string combinedErr = (createRes.StdOut + "\n" + createRes.StdErr).ToLowerInvariant();
                if (combinedErr.Contains("already exists") || combinedErr.Contains("already_exists"))
                {
                    AppendPublishLog($"[Notice] Release or tag {tag} already exists on GitHub.");
                    AppendPublishLog($"Re-uploading asset with --clobber to replace previous MSI...");

                    string uploadArgs = $"release upload {tag} \"{_selectedMsiPath}\" --clobber -R \"{repo}\"";
                    var uploadRes = await RunCliProcessAsync(ghPath, uploadArgs, line => AppendPublishLog(line));
                    if (uploadRes.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"Asset re-upload failed: {uploadRes.StdErr}");
                    }

                    AppendPublishLog($"Updating release metadata and marking as latest...");
                    string editArgs = $"release edit {tag} --title \"{title}\" -F \"{notesTempFile}\" --latest -R \"{repo}\"";
                    var editRes = await RunCliProcessAsync(ghPath, editArgs, line => AppendPublishLog(line));
                    if (editRes.ExitCode != 0)
                    {
                        AppendPublishLog($"[Warning] Release edit warning: {editRes.StdErr}");
                    }
                }
                else
                {
                    throw new InvalidOperationException($"GitHub release creation failed: {createRes.StdErr}");
                }
            }

            AppendPublishLog($"✅ GitHub release {tag} published successfully!");

            // Step 3: Broadcast Cloud Policy to Firestore if enabled
            string directDownloadUrl = $"https://github.com/{repo}/releases/download/{tag}/{msiInfo.Name}";
            if (syncPolicy)
            {
                AppendPublishLog("Broadcasting software policy to Firestore cloud database...");
                var policy = new AppVersionPolicy
                {
                    LatestVersion = cleanVersion,
                    MinRequiredVersion = minVersion,
                    BlockedVersions = TxtPolicyBlockedVersions.Text.Trim(),
                    ForceUpdate = forceUpdate,
                    CustomUpdateMessage = customNotice,
                    GithubRepo = repo,
                    UpdateDownloadUrl = directDownloadUrl,
                    UpdateChangelog = changelog,
                    LastUpdatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                bool cloudOk = await FirebaseCloudAuthService.Instance.SetVersionPolicyAsync(policy);
                if (cloudOk)
                {
                    AppendPublishLog($"✅ Cloud Policy broadcasted! Latest version is now {cleanVersion}.");
                    // Update manual fields to reflect new policy
                    TxtPolicyLatestVersion.Text = cleanVersion;
                    TxtPolicyMinVersion.Text = minVersion;
                    TxtPolicyDownloadUrl.Text = directDownloadUrl;
                    TxtPolicyChangelog.Text = changelog;
                    ChkPolicyForceUpdate.IsChecked = forceUpdate;
                    TxtPolicyCustomMessage.Text = customNotice;
                }
                else
                {
                    AppendPublishLog("⚠️ Warning: GitHub upload succeeded, but Firestore policy broadcast failed. Please check internet connection.");
                }
            }

            AppendPublishLog($"══════════════════════════════════════════════════════════════");
            AppendPublishLog($"🎉 ALL OPERATIONS COMPLETED SUCCESSFULLY!");
            AppendPublishLog($"Release URL: https://github.com/{repo}/releases/tag/{tag}");
            AppendPublishLog($"Download URL: {directDownloadUrl}");
            AppendPublishLog($"══════════════════════════════════════════════════════════════");

            await RefreshDataAsync();

            System.Windows.MessageBox.Show(
                $"Release {tag} published successfully!\n\n" +
                $"• GitHub Release: https://github.com/{repo}/releases/tag/{tag}\n" +
                $"• Direct Download: {directDownloadUrl}\n" +
                $"• Cloud Policy: {(syncPolicy ? "Synchronized to Firestore" : "Manual policy unchanged")}\n\n" +
                "All user computers will now detect and download this update automatically.",
                "Release Published Successfully",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to publish release");
            AppendPublishLog($"❌ ERROR: {ex.Message}");
            System.Windows.MessageBox.Show($"Publication failed: {ex.Message}", "Publication Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try
            {
                if (File.Exists(notesTempFile)) File.Delete(notesTempFile);
            }
            catch { }

            _isPublishing = false;
            BtnPublishRelease.IsEnabled = true;
            ProgressBarPublish.Visibility = Visibility.Collapsed;
        }
    }

    private async void SaveVersionPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (!FirebaseCloudAuthService.BypassForTests && !FirebaseCloudAuthService.Instance.IsSuperAdmin)
        {
            System.Windows.MessageBox.Show("Security Violation: Only Super Admin is authorized to change version policy.", "Security Error", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        string latest = TxtPolicyLatestVersion.Text.Trim();
        string min = TxtPolicyMinVersion.Text.Trim();
        string blocked = TxtPolicyBlockedVersions.Text.Trim();
        bool force = ChkPolicyForceUpdate.IsChecked == true;
        string customMsg = TxtPolicyCustomMessage.Text.Trim();
        string repo = TxtPolicyGithubRepo.Text.Trim();
        string url = TxtPolicyDownloadUrl.Text.Trim();
        string changelog = TxtPolicyChangelog.Text.Trim();

        if (string.IsNullOrEmpty(latest))
        {
            System.Windows.MessageBox.Show("Please enter the Latest Release Version.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtPolicyLatestVersion.Focus();
            return;
        }

        if (string.IsNullOrEmpty(min))
        {
            System.Windows.MessageBox.Show("Please enter the Minimum Required Version.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtPolicyMinVersion.Focus();
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var policy = new AppVersionPolicy
            {
                LatestVersion = latest,
                MinRequiredVersion = min,
                BlockedVersions = blocked,
                ForceUpdate = force,
                CustomUpdateMessage = customMsg,
                GithubRepo = string.IsNullOrEmpty(repo) ? AppUpdateService.DefaultGithubRepo : repo,
                UpdateDownloadUrl = url,
                UpdateChangelog = changelog,
                LastUpdatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            bool ok = await FirebaseCloudAuthService.Instance.SetVersionPolicyAsync(policy);
            if (ok)
            {
                OverlayVersionPolicy.Visibility = Visibility.Collapsed;
                await RefreshDataAsync();
                System.Windows.MessageBox.Show(
                    $"Version Policy broadcasted successfully!\n\nLatest Version: {latest}\nMinimum Version: {min}\nForce Update Lockout: {(force ? "ENABLED" : "Disabled")}\n\nClient PCs will enforce this policy within minutes.",
                    "Version Policy Saved & Broadcasted", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("Failed to save version policy to cloud. Please check your internet connection.", "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    #endregion

    #region Specs Dossier Modal Logic

    private void ViewSpecs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is UserRowViewModel vm)
        {
            _selectedSpecsUser = vm;
            var u = vm.RawUser;

            TxtSpecsHeaderUser.Text = $"User: {(string.IsNullOrWhiteSpace(u.Name) ? "Desktop User" : u.Name)} • {u.Email}";
            TxtSpecsHwId.Text = string.IsNullOrEmpty(u.DeviceId) ? "UNBOUND (Awaiting First Login)" : u.DeviceId;
            TxtSpecsModel.Text = $"Device Model: {(string.IsNullOrEmpty(u.DeviceModel) ? "Not Bound Yet" : u.DeviceModel)}";

            TxtSpecsName.Text = string.IsNullOrWhiteSpace(u.Name) ? "—" : u.Name;
            TxtSpecsEmail.Text = u.Email;
            TxtSpecsOs.Text = string.IsNullOrWhiteSpace(u.OsBuild) ? "—" : u.OsBuild;
            TxtSpecsCpu.Text = string.IsNullOrWhiteSpace(u.CpuModel) ? "—" : u.CpuModel;
            TxtSpecsRam.Text = string.IsNullOrWhiteSpace(u.RamTotal) ? "—" : u.RamTotal;
            TxtSpecsScreen.Text = string.IsNullOrWhiteSpace(u.ScreenRes) ? "—" : u.ScreenRes;
            TxtSpecsWinUser.Text = string.IsNullOrWhiteSpace(u.WindowsUser) ? "—" : u.WindowsUser;
            TxtSpecsMotherboard.Text = string.IsNullOrWhiteSpace(u.Motherboard) ? "—" : u.Motherboard;
            TxtSpecsIp.Text = string.IsNullOrWhiteSpace(u.LocalIp) ? "—" : u.LocalIp;

            OverlaySpecs.Visibility = Visibility.Visible;
        }
    }

    private void CloseSpecs_Click(object sender, RoutedEventArgs e)
    {
        OverlaySpecs.Visibility = Visibility.Collapsed;
    }

    private void CopySpecs_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSpecsUser == null) return;
        var u = _selectedSpecsUser.RawUser;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("══════════════════════════════════════════════");
        sb.AppendLine("   DASMO CYBER CAFE — PC HARDWARE DOSSIER     ");
        sb.AppendLine("══════════════════════════════════════════════");
        sb.AppendLine($"User Name:     {u.Name}");
        sb.AppendLine($"User Email:    {u.Email}");
        sb.AppendLine($"Account Role:  {u.Role} (Status: {u.Status})");
        sb.AppendLine($"Hardware Lock: {u.DeviceId}");
        sb.AppendLine($"Device Model:  {u.DeviceModel}");
        sb.AppendLine($"OS & Build:    {u.OsBuild}");
        sb.AppendLine($"CPU Processor: {u.CpuModel}");
        sb.AppendLine($"RAM Memory:    {u.RamTotal}");
        sb.AppendLine($"Display Res:   {u.ScreenRes}");
        sb.AppendLine($"Windows User:  {u.WindowsUser}");
        sb.AppendLine($"Motherboard:   {u.Motherboard}");
        sb.AppendLine($"Local IP:      {u.LocalIp}");
        if (u.RegistrationTimestamp > 0)
            sb.AppendLine($"Registered:    {DateTimeOffset.FromUnixTimeMilliseconds(u.RegistrationTimestamp).ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (u.LastActiveTimestamp > 0)
            sb.AppendLine($"Last Active:   {DateTimeOffset.FromUnixTimeMilliseconds(u.LastActiveTimestamp).ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("══════════════════════════════════════════════");

        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            System.Windows.MessageBox.Show("All computer hardware specifications copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { }
    }

    #endregion

    private void ApplyFilterAndRender()
    {
        string filter = TxtSearch.Text.Trim().ToLowerInvariant();
        var filtered = _allLoaded;

        if (!string.IsNullOrEmpty(filter))
        {
            filtered = _allLoaded.Where(u =>
                u.Name.ToLowerInvariant().Contains(filter) ||
                u.Email.ToLowerInvariant().Contains(filter) ||
                u.DeviceModel.ToLowerInvariant().Contains(filter) ||
                u.DeviceId.ToLowerInvariant().Contains(filter)).ToList();
        }

        UsersGrid.ItemsSource = filtered;

        int total = _allLoaded.Count;
        int approved = _allLoaded.Count(u => u.RawUser.IsApproved || (u.RawUser.Status ?? "").Equals("approved", StringComparison.OrdinalIgnoreCase));
        int banned = _allLoaded.Count(u => (u.RawUser.Status ?? "").Equals("banned", StringComparison.OrdinalIgnoreCase));
        int pending = total - approved - banned;
        if (pending < 0) pending = 0;

        TxtTotalUsers.Text = $"Total: {total}";
        TxtApprovedUsers.Text = $"Approved: {approved}";
        TxtPendingUsers.Text = $"Pending: {pending}";
        TxtBannedUsers.Text = $"Banned: {banned}";
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilterAndRender();
    }

    private async void ApproveUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is UserRowViewModel vm)
        {
            await FirebaseCloudAuthService.Instance.ApproveUserAsync(_activeCollection, vm.Email);
            await RefreshDataAsync();
        }
    }

    private async void PendingUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is UserRowViewModel vm)
        {
            await FirebaseCloudAuthService.Instance.SetPendingUserAsync(_activeCollection, vm.Email);
            await RefreshDataAsync();
        }
    }

    private async void BanUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is UserRowViewModel vm)
        {
            var res = System.Windows.MessageBox.Show($"Are you sure you want to BAN and block user {vm.Email}?", "Confirm Ban", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                await FirebaseCloudAuthService.Instance.BanUserAsync(_activeCollection, vm.Email);
                await RefreshDataAsync();
            }
        }
    }

    private async void UnbindDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is UserRowViewModel vm)
        {
            var res = System.Windows.MessageBox.Show($"Unbind hardware ID for {vm.Email}?\n\nThis will allow the user to activate this account on a new physical computer.", "Confirm Unbind", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                await FirebaseCloudAuthService.Instance.UnbindDeviceAsync(_activeCollection, vm.Email);
                await RefreshDataAsync();
                System.Windows.MessageBox.Show($"Hardware lock cleared for {vm.Email}. They can now login on their new PC.", "Unbound Successfully", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private async void SetLifetime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is UserRowViewModel vm)
        {
            await FirebaseCloudAuthService.Instance.SetExpiryAsync(_activeCollection, vm.Email, 0L);
            await RefreshDataAsync();
            System.Windows.MessageBox.Show($"User {vm.Email} granted Lifetime license!", "Lifetime Access", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is UserRowViewModel vm)
        {
            var res = System.Windows.MessageBox.Show(
                $"Are you sure you want to PERMANENTLY DELETE user {vm.Email} ({vm.Name})?\n\nThis will completely erase their account and license from the cloud database.",
                "Confirm Permanent Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    bool ok = await FirebaseCloudAuthService.Instance.DeleteUserAsync(_activeCollection, vm.Email);
                    if (ok)
                    {
                        await RefreshDataAsync();
                        System.Windows.MessageBox.Show($"User {vm.Email} was permanently deleted.", "Deleted", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        System.Windows.MessageBox.Show($"Failed to delete user {vm.Email}.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }
            }
        }
    }
}
