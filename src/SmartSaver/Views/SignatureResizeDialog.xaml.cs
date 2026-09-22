using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class SignatureResizeDialog : Window
{
    private readonly SignatureResizeViewModel _viewModel;

    public SignatureResizeDialog(string initialPath = "")
    {
        InitializeComponent();
        _viewModel = new SignatureResizeViewModel(initialPath)
        {
            RequestClose = () => Close()
        };
        DataContext = _viewModel;
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
            if (files != null && files.Length > 0 && File.Exists(files[0]))
            {
                _viewModel.LoadFile(files[0]);
            }
        }
    }
}
