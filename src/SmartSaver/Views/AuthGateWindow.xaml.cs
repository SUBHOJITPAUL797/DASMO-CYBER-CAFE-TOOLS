using System;
using System.Windows;
using System.Windows.Input;
using SmartSaver.Services;
using Serilog;

namespace SmartSaver.Views;

public partial class AuthGateWindow : Window
{
    private string _currentEmail = string.Empty;
    private string _currentName = string.Empty;
    public bool IsApprovedAndReady { get; private set; }

    public AuthGateWindow()
    {
        InitializeComponent();
        TxtHardwareId.Text = HardwareIdService.GetHardwareId();
        TxtDeviceModel.Text = HardwareIdService.GetDeviceModel();

        // Check if there was an active session
        var cached = FirebaseCloudAuthService.Instance.CurrentUser;
        if (cached != null && !string.IsNullOrEmpty(cached.Email))
        {
            TxtEmail.Text = cached.Email;
            if (!string.IsNullOrEmpty(cached.Name))
                TxtName.Text = cached.Name;
            UpdateUiForStatus(FirebaseCloudAuthService.Instance.CurrentStatus, cached);
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        FirebaseCloudAuthService.Instance.StopPolling();
        System.Windows.Application.Current.Shutdown();
    }

    private void CopyHwId_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(TxtHardwareId.Text);
            System.Windows.MessageBox.Show("Hardware ID copied to clipboard! Share this with Admin Subhojit Paul.", "DASMO Security", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { }
    }

    private void PhoneContact_MouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            string emailText = string.IsNullOrWhiteSpace(TxtEmail.Text) ? "my account" : TxtEmail.Text.Trim();
            var res = System.Windows.MessageBox.Show(
                "Contact Admin Subhojit Paul on WhatsApp?\n\nPhone: +91 8927408840\n\nClick 'Yes' to open WhatsApp, or 'No' to copy phone number to clipboard.",
                "Admin Contact", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                string msg = Uri.EscapeDataString($"Hello Subhojit Sir, I am registering on DASMO CYBER CAFE TOOLS on my PC ({TxtHardwareId.Text}). Please approve {emailText}.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"https://wa.me/918927408840?text={msg}",
                    UseShellExecute = true
                });
            }
            else if (res == MessageBoxResult.No)
            {
                System.Windows.Clipboard.SetText("8927408840");
                System.Windows.MessageBox.Show("Phone number '8927408840' copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch
        {
            try
            {
                System.Windows.Clipboard.SetText("8927408840");
            }
            catch { }
        }
    }

    private void EmailContact_MouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            string emailText = string.IsNullOrWhiteSpace(TxtEmail.Text) ? "my account" : TxtEmail.Text.Trim();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"mailto:subhojitpaul26042004@gmail.com?subject={Uri.EscapeDataString("DASMO PC Suite License Approval Request")}&body={Uri.EscapeDataString($"Hello Subhojit,\n\nPlease approve my account on DASMO CYBER CAFE TOOLS.\n\nAccount Email: {emailText}\nHardware ID: {TxtHardwareId.Text}\nPC Model: {TxtDeviceModel.Text}")}",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                System.Windows.Clipboard.SetText("subhojitpaul26042004@gmail.com");
                System.Windows.MessageBox.Show("Email 'subhojitpaul26042004@gmail.com' copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }
    }

    private void TxtEmail_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Login_Click(sender, e);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        string name = TxtName.Text.Trim();
        string email = TxtEmail.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            System.Windows.MessageBox.Show("Please enter your Full Name to register this PC.", "Full Name Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtName.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            System.Windows.MessageBox.Show("Please enter a valid email address.", "Invalid Email", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtEmail.Focus();
            return;
        }

        _currentEmail = email;
        _currentName = name;
        SetPane(PaneLoading);
        TxtLoadingMessage.Text = "Verifying physical hardware license with Firebase Cloud...";

        var status = await FirebaseCloudAuthService.Instance.LoginAsync(email, name, isNewRegistration: true);
        if (status == CloudAuthStatus.NotLoggedIn)
        {
            SetPane(PaneLogin);
            string error = FirebaseCloudAuthService.Instance.LastErrorMessage ?? "Unable to connect to Firebase Cloud licensing server. Please check your internet connection.";
            System.Windows.MessageBox.Show(error, "License Verification", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateUiForStatus(status, FirebaseCloudAuthService.Instance.CurrentUser);
    }

    private async void CheckStatusNow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentEmail)) return;
        SetPane(PaneLoading);
        TxtLoadingMessage.Text = "Checking latest admin approval status...";

        var status = await FirebaseCloudAuthService.Instance.LoginAsync(_currentEmail, _currentName, isNewRegistration: false);
        UpdateUiForStatus(status, FirebaseCloudAuthService.Instance.CurrentUser);
    }

    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        FirebaseCloudAuthService.Instance.Logout();
        _currentEmail = string.Empty;
        _currentName = string.Empty;
        TxtEmail.Text = string.Empty;
        TxtName.Text = string.Empty;
        SetPane(PaneLogin);
        TxtName.Focus();
    }

    private void UpdateUiForStatus(CloudAuthStatus status, CloudUserAccount? user)
    {
        switch (status)
        {
            case CloudAuthStatus.Approved:
                IsApprovedAndReady = true;
                FirebaseCloudAuthService.Instance.StopPolling();
                DialogResult = true;
                Close();
                break;

            case CloudAuthStatus.PendingApproval:
                TxtPendingEmail.Text = user?.Email ?? _currentEmail;
                SetPane(PanePending);
                // Start listening in real-time
                FirebaseCloudAuthService.Instance.StartPollingForApproval(_currentEmail, newStatus =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (newStatus == CloudAuthStatus.Approved)
                        {
                            IsApprovedAndReady = true;
                            DialogResult = true;
                            Close();
                        }
                        else
                        {
                            UpdateUiForStatus(newStatus, FirebaseCloudAuthService.Instance.CurrentUser);
                        }
                    });
                });
                break;

            case CloudAuthStatus.DeviceMismatch:
                FirebaseCloudAuthService.Instance.StopPolling();
                SetPane(PaneMismatch);
                break;

            case CloudAuthStatus.Banned:
                FirebaseCloudAuthService.Instance.StopPolling();
                SetPane(PaneBanned);
                break;

            default:
                SetPane(PaneLogin);
                break;
        }
    }

    private void SetPane(UIElement activePane)
    {
        PaneLogin.Visibility = Visibility.Collapsed;
        PaneLoading.Visibility = Visibility.Collapsed;
        PanePending.Visibility = Visibility.Collapsed;
        PaneMismatch.Visibility = Visibility.Collapsed;
        PaneBanned.Visibility = Visibility.Collapsed;

        activePane.Visibility = Visibility.Visible;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!IsApprovedAndReady && !FirebaseCloudAuthService.BypassForTests)
        {
            FirebaseCloudAuthService.Instance.StopPolling();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
