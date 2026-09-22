using System;
using System.IO;
using System.Windows;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class PassportStudioDialog : Window
{
    public PassportStudioViewModel ViewModel { get; }

    public PassportStudioDialog(string initialPath = "")
    {
        InitializeComponent();
        ViewModel = new PassportStudioViewModel(initialPath);
        DataContext = ViewModel;
        Closing += Window_Closing;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
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

    public static void ShowStudio(string initialPath = "", Window? owner = null)
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

            var dlg = new PassportStudioDialog(initialPath);
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
