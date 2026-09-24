using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using SmartSaver.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace SmartSaver.Views;

public partial class PrintTrackerStudioWindow : Window
{
    public PrintTrackerViewModel Vm { get; }

    public PrintTrackerStudioWindow()
    {
        InitializeComponent();
        Vm = new PrintTrackerViewModel();
        DataContext = Vm;

        Loaded += (_, _) =>
        {
            if (WindowState != WindowState.Maximized)
            {
                EnsureOnScreen();
            }
            UpdateMaximizeState();
        };

        StateChanged += (_, _) => UpdateMaximizeState();
    }

    private void UpdateMaximizeState()
    {
        if (BtnMaximize != null)
        {
            BtnMaximize.Content = WindowState == WindowState.Maximized ? "🗗" : "🗖";
        }

        if (RootBorder != null)
        {
            // When maximized in WPF with WindowChrome, offset by resize border margin
            // to keep all controls, title bar, and edges strictly within the monitor work area
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        }
    }

    private void EnsureOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        if (Width > wa.Width) Width = Math.Max(MinWidth, wa.Width - 40);
        if (Height > wa.Height) Height = Math.Max(MinHeight, wa.Height - 40);

        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;

        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;
    }

    public static void ShowStudio()
    {
        try
        {
            var existing = Application.Current?.Windows.OfType<PrintTrackerStudioWindow>().FirstOrDefault();
            if (existing != null)
            {
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }
                existing.Activate();
                existing.Focus();
                return;
            }

            var win = new PrintTrackerStudioWindow();
            win.Show();
            win.Activate();
            win.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch Print Counter Studio: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+Enter or F12 finishes the customer bill instantly
        if ((e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) || e.Key == Key.F12)
        {
            if (Vm.CanFinishBill && Vm.CompleteBillCommand.CanExecute(null))
            {
                Vm.CompleteBillCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            // Close window if no active jobs, or let user continue
            if (!Vm.HasActiveJobs)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
