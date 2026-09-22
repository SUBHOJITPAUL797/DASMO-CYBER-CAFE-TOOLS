using System.Windows;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class FileCompressDialog : Window
{
    public string FilePath { get; }

    public FileCompressDialog(string filePath)
    {
        InitializeComponent();
        FilePath = filePath;
        var vm = new FileCompressViewModel(filePath);
        vm.RequestClose = () => Close();
        DataContext = vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
