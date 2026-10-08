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

    private void CopiesTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _viewModel?.NormalizeCopiesText();
    }

    private void CustomScaleTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _viewModel?.NormalizeCustomScaleText();
    }

    public static bool ShowPrintDialog(IReadOnlyList<string> imagePaths, string documentTitle = "DASMO Print Job", Window? owner = null, string? preferredPaperSize = null, string? preferredOrientation = null)
    {
        var dlg = new NativePrintDialog(imagePaths, documentTitle, preferredPaperSize, preferredOrientation);
        Window? targetOwner = owner;
        if (targetOwner == null || !targetOwner.IsVisible || targetOwner.WindowState == WindowState.Minimized)
        {
            targetOwner = WpfApplication.Current?.Windows.OfType<Window>()
                .FirstOrDefault(w => w.IsActive && w.IsVisible && !(w is NativePrintDialog))
                ?? WpfApplication.Current?.Windows.OfType<Window>()
                .LastOrDefault(w => w.IsVisible && w.WindowState != WindowState.Minimized && !(w is NativePrintDialog))
                ?? WpfApplication.Current?.MainWindow;
        }

        if (targetOwner != null && targetOwner.IsVisible && targetOwner.WindowState != WindowState.Minimized)
        {
            dlg.Owner = targetOwner;
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
        Window? targetOwner = owner;
        if (targetOwner == null || !targetOwner.IsVisible || targetOwner.WindowState == WindowState.Minimized)
        {
            targetOwner = WpfApplication.Current?.Windows.OfType<Window>()
                .FirstOrDefault(w => w.IsActive && w.IsVisible && !(w is NativePrintDialog))
                ?? WpfApplication.Current?.Windows.OfType<Window>()
                .LastOrDefault(w => w.IsVisible && w.WindowState != WindowState.Minimized && !(w is NativePrintDialog))
                ?? WpfApplication.Current?.MainWindow;
        }

        if (targetOwner != null && targetOwner.IsVisible && targetOwner.WindowState != WindowState.Minimized)
        {
            dlg.Owner = targetOwner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dlg.ShowDialog() == true;
    }
}
