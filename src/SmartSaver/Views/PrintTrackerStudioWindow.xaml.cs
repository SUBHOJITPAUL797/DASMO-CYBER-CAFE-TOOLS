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
            // Clamp to work area on first load
            EnsureOnScreen();
            UpdateMaximizeState();
        };

        StateChanged += (_, _) =>
        {
            UpdateMaximizeState();
            // After restoring from maximized, re-clamp to work area
            if (WindowState == WindowState.Normal)
            {
                Dispatcher.BeginInvoke(EnsureOnScreen);
            }
        };

        // Ensure window never exceeds work area height (prevents taskbar overlap)
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                var wa = SystemParameters.WorkArea;
                if (Height > wa.Height)
                    Height = wa.Height;
                if (Top < wa.Top)
                    Top = wa.Top;
                if (Top + Height > wa.Bottom)
                    Top = Math.Max(wa.Top, wa.Bottom - Height);
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
                // WindowStyle=None + AllowsTransparency=False: WPF maximizes to FULL screen
                // including the taskbar. We must constrain ourselves to the WorkArea.
                // Setting MaxHeight = WorkArea.Height tells WPF to never exceed that height.
                var wa = SystemParameters.WorkArea;
                MaxHeight = wa.Height;

                // The Margin(7) on RootBorder compensates for WindowChrome resize handle thickness
                // so controls don't bleed off-screen edges. This is the standard WPF technique.
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

        // Clamp size to fit within work area (leave 20px breathing room on each side)
        if (Width  > wa.Width)  Width  = Math.Max(MinWidth,  wa.Width  - 20);
        if (Height > wa.Height) Height = Math.Max(MinHeight, wa.Height - 20);

        // Center on work area
        double newLeft = wa.Left + (wa.Width  - Width)  / 2.0;
        double newTop  = wa.Top  + (wa.Height - Height) / 2.0;

        // Hard clamps — window must not go outside the work area on any edge
        Left = Math.Max(wa.Left, Math.Min(newLeft, wa.Right  - Width));
        Top  = Math.Max(wa.Top,  Math.Min(newTop,  wa.Bottom - Height));
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

    private void OpenRateSettings_Click(object sender, RoutedEventArgs e)
    {
        if (RateSettingsModal != null)
            RateSettingsModal.Visibility = Visibility.Visible;
    }

    private void CloseRateSettings_Click(object sender, RoutedEventArgs e)
    {
        if (RateSettingsModal != null)
            RateSettingsModal.Visibility = Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (RateSettingsModal != null && RateSettingsModal.Visibility == Visibility.Visible)
            {
                RateSettingsModal.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

            // Close window if no active jobs, or let user continue
            if (!Vm.HasActiveJobs)
            {
                Close();
                e.Handled = true;
            }
            return;
        }

        // Ctrl+Enter or F12 finishes the customer bill instantly
        if ((e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) || e.Key == Key.F12)
        {
            if (Vm.CanFinishBill && Vm.CompleteBillCommand.CanExecute(null))
            {
                Vm.CompleteBillCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
