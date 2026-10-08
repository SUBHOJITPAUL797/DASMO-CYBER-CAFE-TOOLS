using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SmartSaver.ViewModels;
using WpfApplication = System.Windows.Application;

namespace SmartSaver.Views;

public partial class NativePrintDialog : Window
{
    private readonly NativePrintViewModel _viewModel;

    public NativePrintDialog(IReadOnlyList<string> imagePaths, string documentTitle = "DASMO Print Job", string? preferredPaperSize = null, string? preferredOrientation = null)
    {
        InitializeComponent();
        _viewModel = new NativePrintViewModel(imagePaths, documentTitle, preferredPaperSize, preferredOrientation)
        {
            GetWindowHandle = () => new WindowInteropHelper(this).Handle,
            RequestClose = success =>
            {
                DialogResult = success;
                Close();
            }
        };
        DataContext = _viewModel;
    }

    public NativePrintDialog(ImageSource singleImage, string documentTitle = "DASMO Print Job", string? preferredPaperSize = null, string? preferredOrientation = null)
    {
        InitializeComponent();
        _viewModel = new NativePrintViewModel(singleImage, documentTitle, preferredPaperSize, preferredOrientation)
        {
            GetWindowHandle = () => new WindowInteropHelper(this).Handle,
            RequestClose = success =>
            {
                DialogResult = success;
                Close();
            }
        };
        DataContext = _viewModel;
    }

    public static bool ShowPrintDialog(IReadOnlyList<string> imagePaths, string documentTitle = "DASMO Print Job", Window? owner = null, string? preferredPaperSize = null, string? preferredOrientation = null)
    {
        var dlg = new NativePrintDialog(imagePaths, documentTitle, preferredPaperSize, preferredOrientation);
        if (owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else if (WpfApplication.Current?.MainWindow != null && WpfApplication.Current.MainWindow.IsVisible && WpfApplication.Current.MainWindow.WindowState != WindowState.Minimized)
        {
            dlg.Owner = WpfApplication.Current.MainWindow;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dlg.ShowDialog() == true;
    }

    public static bool ShowPrintDialog(ImageSource singleImage, string documentTitle = "DASMO Print Job", Window? owner = null, string? preferredPaperSize = null, string? preferredOrientation = null)
    {
        var dlg = new NativePrintDialog(singleImage, documentTitle, preferredPaperSize, preferredOrientation);
        if (owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else if (WpfApplication.Current?.MainWindow != null && WpfApplication.Current.MainWindow.IsVisible && WpfApplication.Current.MainWindow.WindowState != WindowState.Minimized)
        {
            dlg.Owner = WpfApplication.Current.MainWindow;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dlg.ShowDialog() == true;
    }
}
