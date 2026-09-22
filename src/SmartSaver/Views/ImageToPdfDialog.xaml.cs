using System.Windows;
using System.Windows.Input;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class ImageToPdfDialog : Window
{
    public ImageToPdfDialog(IEnumerable<string>? initialFiles = null)
    {
        InitializeComponent();
        var vm = new ImageToPdfViewModel(initialFiles);
        vm.RequestClose = () => Close();
        DataContext = vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (DataContext is ImageToPdfViewModel vm)
            {
                foreach (var f in files) vm.AddFile(f);
            }
        }
    }
}
