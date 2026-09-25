using System.Windows;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class PrinterAuditDialog : Window
{
    private static PrinterAuditDialog? _activeInstance;

    public PrinterAuditDialog()
    {
        InitializeComponent();
        var vm = new PrinterAuditViewModel();
        vm.RequestClose = () => Close();
        DataContext = vm;
    }

    public static void ShowPrinterAudit()
    {
        if (_activeInstance != null && _activeInstance.IsLoaded)
        {
            if (_activeInstance.WindowState == WindowState.Minimized)
            {
                _activeInstance.WindowState = WindowState.Normal;
            }
            _activeInstance.Activate();
            return;
        }

        _activeInstance = new PrinterAuditDialog();
        _activeInstance.Closed += (_, _) => _activeInstance = null;
        _activeInstance.Show();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
