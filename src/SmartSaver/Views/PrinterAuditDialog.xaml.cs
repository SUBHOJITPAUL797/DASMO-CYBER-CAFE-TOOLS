using System.Windows;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class PrinterAuditDialog : Window
{
    public PrinterAuditDialog()
    {
        InitializeComponent();
        var vm = new PrinterAuditViewModel();
        vm.RequestClose = () => Close();
        DataContext = vm;
    }

    public static void ShowPrinterAudit()
    {
        // Direct integration: Activate Tab 1 (Hardware Meter & Walk-up Xerox Audit) in unified PrintTrackerStudioWindow
        PrintTrackerStudioWindow.ShowStudio(initialTab: 1);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
