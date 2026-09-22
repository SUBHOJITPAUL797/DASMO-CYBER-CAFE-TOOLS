using System.Windows;
using System.Windows.Input;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class ScanEnhancerDialog : Window
{
    public ScanEnhancerDialog(string filePath)
    {
        InitializeComponent();
        var vm = new ScanEnhancerViewModel(filePath);
        vm.RequestClose = () => Close();
        DataContext = vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
