using System.Windows;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class DocumentStackerWindow : Window
{
    private readonly DocumentStackerViewModel _vm;

    public DocumentStackerWindow(string? initialPath = null)
    {
        InitializeComponent();
        _vm = new DocumentStackerViewModel();
        _vm.RequestClose = () => Close();

        if (!string.IsNullOrEmpty(initialPath))
        {
            _vm.FrontPath = initialPath;
        }

        // Wire up all file dialogs safely with owner window
        _vm.RequestBrowseFront = () =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Front Side File",
                    Filter = "Supported files (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.tiff;*.pdf)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.tiff;*.pdf|All Files (*.*)|*.*"
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error in RequestBrowseFront file dialog");
                return null;
            }
        };

        _vm.RequestBrowseBack = () =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Back Side File",
                    Filter = "Supported files (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.tiff;*.pdf)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.tiff;*.pdf|All Files (*.*)|*.*"
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error in RequestBrowseBack file dialog");
                return null;
            }
        };

        _vm.RequestBrowseBoth = () =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select 2 Files (1st = Front, 2nd = Back)",
                    Multiselect = true,
                    Filter = "Supported files (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.tiff;*.pdf)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.tiff;*.pdf|All Files (*.*)|*.*"
                };
                return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error in RequestBrowseBoth file dialog");
                return null;
            }
        };

        _vm.RequestImportPdf = () =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select 2-Page PDF to Import",
                    Filter = "PDF files (*.pdf)|*.pdf"
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error in RequestImportPdf file dialog");
                return null;
            }
        };

        _vm.RequestSaveFile = (filter, defaultName) =>
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = filter,
                    FileName = defaultName
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error in RequestSaveFile file dialog");
                return null;
            }
        };

        DataContext = _vm;
        Closing += Window_Closing;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm == null || !_vm.HasUnsavedWork) return;

        var res = System.Windows.MessageBox.Show(
            "You have document(s) loaded in Document Stacker that have not been saved or stacked.\n\nAre you sure you want to close and discard this session?",
            "Unsaved Documents — A4 Document Stacker",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (res != System.Windows.MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenGovtCardPrint_Click(object sender, RoutedEventArgs e)
    {
        Close();
        App.CurrentApp?.ShowMainWindow(DashboardTab.GovtCardTab);
    }
}
