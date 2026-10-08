using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartSaver.ViewModels;
using Cursors = System.Windows.Input.Cursors;

namespace SmartSaver.Views;

public partial class ExcelPrintStudioDialog : Window
{
    public ExcelPrintStudioViewModel ViewModel { get; }

    public ExcelPrintStudioDialog(string initialPath = "")
    {
        InitializeComponent();
        ViewModel = new ExcelPrintStudioViewModel(initialPath);
        DataContext = ViewModel;

        Loaded += (_, _) =>
        {
            EnsureOnScreen();
            UpdateMaximizeState();
        };

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ExcelPrintStudioViewModel.CurrentPreviewImage))
            {
                OnPreviewImageChanged();
            }

        };

        StateChanged += (_, _) =>
        {
            UpdateMaximizeState();
            if (WindowState == WindowState.Normal)
            {
                Dispatcher.BeginInvoke(EnsureOnScreen);
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel.CurrentPreviewImage != null && PreviewScrollViewer.ViewportWidth > 50)
                {
                    FitPage();
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                var wa = SystemParameters.WorkArea;
                if (Height > wa.Height)
                    Height = Math.Max(MinHeight, wa.Height - 16);
                if (Top < wa.Top)
                    Top = wa.Top;
                if (Top + Height > wa.Bottom)
                    Top = Math.Max(wa.Top, wa.Bottom - Height);
                if (Left < wa.Left)
                    Left = wa.Left;
                if (Left + Width > wa.Right)
                    Left = Math.Max(wa.Left, wa.Right - Width);
            }
            if (ViewModel.CurrentPreviewImage != null && PreviewScrollViewer.ViewportWidth > 50)
            {
                FitPage();
            }
        };

        PreviewScrollViewer.SizeChanged += (_, _) =>
        {
            if (ViewModel.CurrentPreviewImage != null)
            {
                FitPage();
            }
        };
    }

    private void OnPreviewImageChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ViewModel.CurrentPreviewImage != null)
            {
                // Default to FitWidth so the spreadsheet spans edge-to-edge across the reader pane
                // just like Microsoft Edge and Adobe Acrobat, maximizing column clarity and readability.
                FitWidth();
            }
            UpdatePreviewCursor();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FitWidth_Click(object sender, RoutedEventArgs e) => FitWidth();
    private void FitPage_Click(object sender, RoutedEventArgs e) => FitPage();

    public void FitWidth()
    {
        if (ViewModel.CurrentPreviewImage == null) return;
        double vWidth = PreviewScrollViewer.ViewportWidth > 50 ? PreviewScrollViewer.ViewportWidth : PreviewScrollViewer.ActualWidth;
        if (vWidth <= 50) return;
        double bmpWidth = ViewModel.CurrentPreviewImage.Width;
        if (bmpWidth <= 0) return;

        // Deduct 36px (16px margin on each side + 4px breathing room)
        // so the paper spans edge-to-edge across the preview canvas exactly like Edge reader
        double availableWidth = Math.Max(100, vWidth - 36);
        ViewModel.ZoomFactor = availableWidth / bmpWidth;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            PreviewScrollViewer.ScrollToHorizontalOffset(0);
            PreviewScrollViewer.ScrollToVerticalOffset(0);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void FitPage()
    {
        if (ViewModel.CurrentPreviewImage == null) return;
        double vWidth = PreviewScrollViewer.ViewportWidth > 50 ? PreviewScrollViewer.ViewportWidth : PreviewScrollViewer.ActualWidth;
        double vHeight = PreviewScrollViewer.ViewportHeight > 50 ? PreviewScrollViewer.ViewportHeight : PreviewScrollViewer.ActualHeight;
        if (vWidth <= 50 || vHeight <= 50) return;

        double bmpWidth = ViewModel.CurrentPreviewImage.Width;
        double bmpHeight = ViewModel.CurrentPreviewImage.Height;
        if (bmpWidth <= 0 || bmpHeight <= 0) return;

        // Deduct 36px for clean 16px margins on all sides
        double availableWidth = Math.Max(100, vWidth - 36);
        double availableHeight = Math.Max(100, vHeight - 36);

        double scaleW = availableWidth / bmpWidth;
        double scaleH = availableHeight / bmpHeight;
        ViewModel.ZoomFactor = Math.Min(scaleW, scaleH);
        CenterScrollViewer();
    }

    private void CenterScrollViewer()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PreviewScrollViewer.ScrollToHorizontalOffset(
                Math.Max(0, (PreviewScrollViewer.ExtentWidth - PreviewScrollViewer.ViewportWidth) / 2.0));
            PreviewScrollViewer.ScrollToVerticalOffset(
                Math.Max(0, (PreviewScrollViewer.ExtentHeight - PreviewScrollViewer.ViewportHeight) / 2.0));
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Freeform Mouse Drag Panning (Hand Pan Tool) ──
    private bool _isPanning;
    private System.Windows.Point _panStartPoint;
    private double _panStartOffsetH;
    private double _panStartOffsetV;

    private void PreviewScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.HasDocument && (PreviewScrollViewer.ExtentWidth > PreviewScrollViewer.ViewportWidth || PreviewScrollViewer.ExtentHeight > PreviewScrollViewer.ViewportHeight))
        {
            _isPanning = true;
            _panStartPoint = e.GetPosition(PreviewScrollViewer);
            _panStartOffsetH = PreviewScrollViewer.HorizontalOffset;
            _panStartOffsetV = PreviewScrollViewer.VerticalOffset;
            PreviewScrollViewer.Cursor = Cursors.SizeAll;
            PreviewScrollViewer.CaptureMouse();
            e.Handled = true;
        }
    }

    private void PreviewScrollViewer_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning) return;

        var currentPos = e.GetPosition(PreviewScrollViewer);
        double deltaX = _panStartPoint.X - currentPos.X;
        double deltaY = _panStartPoint.Y - currentPos.Y;

        PreviewScrollViewer.ScrollToHorizontalOffset(_panStartOffsetH + deltaX);
        PreviewScrollViewer.ScrollToVerticalOffset(_panStartOffsetV + deltaY);
        e.Handled = true;
    }

    private void PreviewScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;

        _isPanning = false;
        PreviewScrollViewer.ReleaseMouseCapture();
        UpdatePreviewCursor();
        e.Handled = true;
    }

    private void PreviewScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_isPanning)
        {
            UpdatePreviewCursor();
        }
    }

    private void UpdatePreviewCursor()
    {
        if (PreviewScrollViewer == null) return;
        bool canScroll = PreviewScrollViewer.ExtentWidth > PreviewScrollViewer.ViewportWidth ||
                         PreviewScrollViewer.ExtentHeight > PreviewScrollViewer.ViewportHeight;
        PreviewScrollViewer.Cursor = canScroll ? Cursors.Hand : Cursors.Arrow;
    }

    private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl + Mouse Wheel = Smooth Zoom In / Zoom Out
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Delta > 0)
                ViewModel.ZoomFactor += 0.1;
            else if (e.Delta < 0)
                ViewModel.ZoomFactor -= 0.1;
            e.Handled = true;
            return;
        }

        // Shift + Mouse Wheel = Horizontal Left / Right Scroll
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (e.Delta > 0)
                PreviewScrollViewer.ScrollToHorizontalOffset(PreviewScrollViewer.HorizontalOffset - 60);
            else if (e.Delta < 0)
                PreviewScrollViewer.ScrollToHorizontalOffset(PreviewScrollViewer.HorizontalOffset + 60);
            e.Handled = true;
            return;
        }
    }

    private void EnsureOnScreen()
    {
        var wa = SystemParameters.WorkArea;

        // Auto-scale window to screen resolution with safe margin
        if (Width > wa.Width - 20)
            Width = Math.Max(MinWidth, wa.Width - 20);

        if (Height > wa.Height - 20)
            Height = Math.Max(MinHeight, wa.Height - 20);

        // Center within available work area
        double newLeft = wa.Left + (wa.Width - Width) / 2.0;
        double newTop = wa.Top + (wa.Height - Height) / 2.0;

        // Hard clamping: Top CANNOT be less than wa.Top (guarantees title bar stays on-screen!)
        Left = Math.Max(wa.Left, Math.Min(newLeft, wa.Right - Width));
        Top = Math.Max(wa.Top, Math.Min(newTop, wa.Bottom - Height));
    }

    private void UpdateMaximizeState()
    {
        if (BtnMaximize != null)
        {
            BtnMaximize.Content = WindowState == WindowState.Maximized ? "🗗" : "🗖";
            BtnMaximize.ToolTip = WindowState == WindowState.Maximized ? "Restore Down" : "Maximize (Full Screen)";
        }

        if (RootBorder != null)
        {
            if (WindowState == WindowState.Maximized)
            {
                var wa = SystemParameters.WorkArea;
                MaxHeight = wa.Height;
                MaxWidth = wa.Width;
                RootBorder.Margin = new Thickness(7);
                RootBorder.BorderThickness = new Thickness(0);
            }
            else
            {
                MaxHeight = double.PositiveInfinity;
                MaxWidth = double.PositiveInfinity;
                RootBorder.Margin = new Thickness(0);
                RootBorder.BorderThickness = new Thickness(1);
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch { }
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

    public static void ShowStudio(string initialPath = "", Window? owner = null)
    {
        try
        {
            // If already open, activate and bring to front
            foreach (Window w in System.Windows.Application.Current.Windows)
            {
                if (w is ExcelPrintStudioDialog existing && existing.IsLoaded)
                {
                    if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
                    {
                        existing.ViewModel.LoadFile(initialPath);
                    }
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;
                    existing.Activate();
                    existing.Focus();
                    return;
                }
            }

            var dlg = new ExcelPrintStudioDialog(initialPath);
            if (owner != null)
            {
                dlg.Owner = owner;
            }
            dlg.Show();
            dlg.Activate();
            dlg.Focus();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open ExcelPrintStudioDialog");
            System.Windows.MessageBox.Show($"Could not open Excel Print Studio:\n{ex.Message}", "Studio Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RangeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tag)
        {
            ViewModel.RangeMode = tag;
        }
    }

    private void ColumnCheck_Changed(object sender, RoutedEventArgs e)
    {
        ViewModel.SchedulePreviewUpdate();
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && File.Exists(files[0]))
            {
                ViewModel.LoadFile(files[0]);
            }
        }
    }
}
