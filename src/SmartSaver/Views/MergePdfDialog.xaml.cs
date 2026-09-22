using System.IO;
using System.Windows;
using System.Windows.Input;
using SmartSaver.ViewModels;
using WDragEventArgs = System.Windows.DragEventArgs;
using WDataFormats = System.Windows.DataFormats;
using WDragDropEffects = System.Windows.DragDropEffects;

namespace SmartSaver.Views;

public partial class MergePdfDialog : Window
{
    private readonly MergePdfViewModel _viewModel;

    public MergePdfDialog(IEnumerable<string>? initialFiles = null)
    {
        InitializeComponent();
        _viewModel = new MergePdfViewModel(initialFiles);
        _viewModel.RequestClose += (s, e) => Close();
        DataContext = _viewModel;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_DragOver(object sender, WDragEventArgs e)
    {
        if (e.Data.GetDataPresent(WDataFormats.FileDrop))
        {
            e.Effects = WDragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = WDragDropEffects.None;
        }
    }

    private void Window_Drop(object sender, WDragEventArgs e)
    {
        if (e.Data.GetDataPresent(WDataFormats.FileDrop))
        {
            if (e.Data.GetData(WDataFormats.FileDrop) is string[] files)
            {
                var pdfFiles = files.Where(f => File.Exists(f) && Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
                _viewModel.AddFiles(pdfFiles);
            }
        }
    }
}
