using System.IO;
using System.Windows;
using System.Windows.Input;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class ImageResizeDialog : Window
{
    public ImageResizeDialog(string filePath)
    {
        InitializeComponent();
        
        var viewModel = new ImageResizeViewModel(filePath);
        viewModel.RequestClose = Close;
        
        DataContext = viewModel;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
