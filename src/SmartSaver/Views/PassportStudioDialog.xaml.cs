using System;
using System.IO;
using System.Windows;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class PassportStudioDialog : Window
{
    public PassportStudioViewModel ViewModel { get; }

    public PassportStudioDialog(string initialPath = "")
        : this(new PassportStudioViewModel(initialPath))
    {
    }

    public PassportStudioDialog(PassportStudioViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = ViewModel;
        Closing += Window_Closing;

        // Auto-fit to screen work area so window borders and resize grip are never off-screen
        try
        {
            var workArea = SystemParameters.WorkArea;
            if (Height > workArea.Height - 30)
                Height = Math.Max(480, workArea.Height - 30);
            if (Width > workArea.Width - 30)
                Width = Math.Max(720, workArea.Width - 30);
        }
        catch { }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool hasParentMainWindow = System.Windows.Application.Current?.MainWindow != null &&
                                    System.Windows.Application.Current.MainWindow.IsVisible &&
                                    System.Windows.Application.Current.MainWindow != this;
        if (hasParentMainWindow) return;

        if (ViewModel == null || !ViewModel.HasUnsavedWork) return;

        var res = System.Windows.MessageBox.Show(
            "You have an active passport photo session that has not been saved or printed.\n\nAre you sure you want to close and discard this session?",
            "Unsaved Photo Session — Passport Studio",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (res != System.Windows.MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private void DimensionTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ViewModel?.NormalizeDimensionTexts();
    }

    private void SingleKbTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ViewModel?.NormalizeSingleKbTexts();
    }

    public static void ShowStudio(string initialPath = "", Window? owner = null, PassportStudioViewModel? existingVm = null)
    {
        try
        {
            // If already open, activate and bring to front
            foreach (Window w in System.Windows.Application.Current.Windows)
            {
                if (w is PassportStudioDialog existing && existing.IsLoaded)
                {
                    if (!string.IsNullOrEmpty(initialPath))
                    {
                        existing.ViewModel.LoadPhoto(initialPath);
                    }
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;
                    existing.Activate();
                    existing.Focus();
                    return;
                }
            }

            var dlg = existingVm != null ? new PassportStudioDialog(existingVm) : new PassportStudioDialog(initialPath);
            if (owner != null)
            {
                dlg.Owner = owner;
            }
            else if (System.Windows.Application.Current?.MainWindow != null && System.Windows.Application.Current.MainWindow.IsVisible)
            {
                dlg.Owner = System.Windows.Application.Current.MainWindow;
            }
            dlg.Show();
            dlg.Activate();
            dlg.Focus();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open PassportStudioDialog");
            System.Windows.MessageBox.Show($"Could not open Passport Studio:\n{ex.Message}", "Passport Studio Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
