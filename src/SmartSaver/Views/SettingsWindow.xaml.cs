using System.IO;
using System.Windows;
using System.Windows.Input;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class SettingsWindow : Window
{
    public event EventHandler? SettingsSaved;

    public SettingsWindow()
    {
        InitializeComponent();
        
        var viewModel = new SettingsViewModel();
        viewModel.RequestClose = Close;
        viewModel.RequestSave = (sender, args) => SettingsSaved?.Invoke(sender, args);
        
        DataContext = viewModel;

        try
        {
            var uri = new Uri("pack://application:,,,/Resources/owner_portrait.png", UriKind.Absolute);
            var img = new System.Windows.Media.Imaging.BitmapImage(uri);
            OwnerPortraitBrush.ImageSource = img;
            OwnerWatermarkImage.Source = img;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not load owner portrait image resource");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
