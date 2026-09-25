using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using SmartSaver.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace SmartSaver.Views;

public partial class CashDrawerWindow : Window
{
    public CashDrawerViewModel Vm { get; }

    public CashDrawerWindow()
    {
        InitializeComponent();
        Vm = new CashDrawerViewModel();
        DataContext = Vm;

        Loaded += (_, _) =>
        {
            EnsureOnScreen();
            UpdateMaximizeState();
        };

        StateChanged += (_, _) =>
        {
            UpdateMaximizeState();
            if (WindowState == WindowState.Normal)
            {
                Dispatcher.BeginInvoke(EnsureOnScreen);
            }
        };

        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                var wa = SystemParameters.WorkArea;
                if (Height > wa.Height) Height = wa.Height;
                if (Top < wa.Top) Top = wa.Top;
                if (Top + Height > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
            }
        };
    }

    private void UpdateMaximizeState()
    {
        if (BtnMaximize != null)
        {
            BtnMaximize.Content = WindowState == WindowState.Maximized ? "🗗" : "🗖";
        }

        if (RootBorder != null)
        {
            if (WindowState == WindowState.Maximized)
            {
                var wa = SystemParameters.WorkArea;
                MaxHeight = wa.Height;
                RootBorder.Margin = new Thickness(7);
                RootBorder.BorderThickness = new Thickness(0);
            }
            else
            {
                MaxHeight = double.PositiveInfinity;
                RootBorder.Margin = new Thickness(0);
                RootBorder.BorderThickness = new Thickness(1);
            }
        }
    }

    private void EnsureOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        if (Width > wa.Width) Width = Math.Max(MinWidth, wa.Width - 30);
        if (Height > wa.Height) Height = Math.Max(MinHeight, wa.Height - 30);

        Left = Math.Max(wa.Left, wa.Left + (wa.Width - Width) / 2.0);
        Top = Math.Max(wa.Top, wa.Top + (wa.Height - Height) / 2.0);
    }

    public static void ShowCashDrawer(Window? owner = null)
    {
        try
        {
            var existing = Application.Current?.Windows.OfType<CashDrawerWindow>().FirstOrDefault();
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

            var win = new CashDrawerWindow();
            if (owner != null && owner.IsVisible)
            {
                win.Owner = owner;
            }
            win.Show();
            win.Activate();
            win.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open Cash Drawer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
}
