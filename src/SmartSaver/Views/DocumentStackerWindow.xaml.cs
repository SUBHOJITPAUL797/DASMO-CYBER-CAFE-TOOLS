using System.Windows;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class DocumentStackerWindow : Window
{
    private readonly DocumentStackerViewModel _vm;

    public DocumentStackerWindow(string? initialPath = null)
        : this(new DocumentStackerViewModel())
    {
        if (!string.IsNullOrEmpty(initialPath))
        {
            _vm.FrontPath = initialPath;
        }
    }

    public DocumentStackerWindow(DocumentStackerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _vm.RequestClose = () => Close();
        SetupDialogCallbacks();
        DataContext = _vm;
        Closing += Window_Closing;

        // Auto-fit to screen work area so window borders and resize grip are never off-screen
        try
        {
            var workArea = SystemParameters.WorkArea;
            if (Height > workArea.Height - 30)
                Height = Math.Max(460, workArea.Height - 30);
            if (Width > workArea.Width - 30)
                Width = Math.Max(500, workArea.Width - 30);
        }
        catch { }
    }

    private Window? GetActiveOwner()
    {
        if (IsLoaded && IsVisible) return this;
        var main = System.Windows.Application.Current?.MainWindow;
        if (main != null && main.IsLoaded && main.IsVisible) return main;
        return null;
    }

    private void SetupDialogCallbacks()
    {
        // Wire up all file dialogs safely with active owner window
        _vm.RequestBrowseFront = () =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Front Side File",
                    Filter = "Supported files (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.tiff;*.pdf)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.tiff;*.pdf|All Files (*.*)|*.*"
                };
                var owner = GetActiveOwner();
                return (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true ? dlg.FileName : null;
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
                var owner = GetActiveOwner();
                return (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true ? dlg.FileName : null;
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
                var owner = GetActiveOwner();
                return (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true ? dlg.FileNames : null;
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
                var owner = GetActiveOwner();
                return (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true ? dlg.FileName : null;
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
                var owner = GetActiveOwner();
                return (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true ? dlg.FileName : null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Error in RequestSaveFile file dialog");
                return null;
            }
        };
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool hasParentMainWindow = System.Windows.Application.Current?.MainWindow != null &&
                                    System.Windows.Application.Current.MainWindow.IsVisible &&
                                    System.Windows.Application.Current.MainWindow != this;
        if (hasParentMainWindow) return;

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
