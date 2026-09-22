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
using Application = System.Windows.Application;
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

    #region Version Policy Modal Logic

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
        OverlayVersionPolicy.Visibility = Visibility.Collapsed;
    }

    private async void SaveVersionPolicy_Click(object sender, RoutedEventArgs e)
    {
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
                UpdateChangelog = changelog
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
