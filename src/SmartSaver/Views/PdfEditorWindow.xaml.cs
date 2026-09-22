using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SmartSaver.Models;
using SmartSaver.ViewModels;
using SmartSaver.Services;
using Serilog;
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SmartSaver.Views;

public partial class PdfEditorWindow : Window
{
    public PdfEditorViewModel Vm => (PdfEditorViewModel)DataContext;

    private WpfPoint _dragStartPoint;
    private bool _isCreatingWhiteout;
    private bool _isDraggingItem;
    private bool _isResizingItem;
    private bool _isDrawingInk;
    private PdfInkItem? _activeInkItem;
    private PdfEditItem? _activeItem;
    private WpfPoint _lastMouseCanvasPos;

    public PdfEditorWindow(string initialPdfPath = "")
    {
        InitializeComponent();
        var vm = new PdfEditorViewModel(initialPdfPath);
        vm.RequestClose = () => Close();
        DataContext = vm;
        Loaded += (_, _) => EnsureOnScreen();
        Closing += Window_Closing;
    }

    private bool _isClosingConfirmed;

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosingConfirmed)
        {
            // Closing was already confirmed (either saved or discarded) — allow WPF to close cleanly
            return;
        }

        if (Vm == null || !Vm.HasUnsavedEdits)
        {
            // No unsaved changes — allow WPF to close immediately
            return;
        }

        var res = System.Windows.MessageBox.Show(
            $"You have unsaved changes in '{Vm.FileName}'.\n\nDo you want to save your work before closing?\n\n• Click Yes to Save & Close\n• Click No to Discard & Close\n• Click Cancel to Keep Window Open",
            "Unsaved Changes — PDF Editor Studio",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);

        if (res == System.Windows.MessageBoxResult.Cancel)
        {
            // Abort closing: keep window open and interactive
            Vm.FlushDraftNow();
            e.Cancel = true;
            return;
        }

        if (res == System.Windows.MessageBoxResult.No)
        {
            // User chose No (Discard & Close):
            // DO NOT cancel the event! e.Cancel is false by default, so WPF will close the window right away!
            _isClosingConfirmed = true;
            Vm.HasUnsavedEdits = false;

            try
            {
                if (!string.IsNullOrEmpty(Vm.FilePath))
                {
                    PdfDraftService.Instance.DeleteDraft(Vm.FilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to cleanup draft on discard for {Path}", Vm.FilePath);
            }

            // Return immediately without cancelling; WPF closes the window immediately
            return;
        }

        if (res == System.Windows.MessageBoxResult.Yes)
        {
            // User chose Yes (Save & Close):
            // Cancel this synchronous close event because saving is async
            e.Cancel = true;

            bool saved = await Vm.SavePdfAsync(saveAsNew: false, reloadAfterSave: false);
            if (saved)
            {
                _isClosingConfirmed = true;
                Vm.HasUnsavedEdits = false;
                Close(); // Trigger clean close
            }
        }
    }

    /// <summary>
    /// Clamps the window so it is fully visible within the working area of whichever
    /// screen it lands on after <see cref="WindowStartupLocation.CenterScreen"/> runs.
    /// Also shrinks the window if it is larger than the working area.
    /// </summary>
    private void EnsureOnScreen()
    {
        var wa = SystemParameters.WorkArea; // primary screen work area (excludes taskbar)

        // Shrink if window is bigger than screen
        if (Width > wa.Width)   Width  = wa.Width;
        if (Height > wa.Height) Height = wa.Height;

        // Re-center after possible shrink
        Left = wa.Left + (wa.Width  - Width)  / 2;
        Top  = wa.Top  + (wa.Height - Height) / 2;

        // Clamp to stay within work area edges
        if (Left < wa.Left)   Left = wa.Left;
        if (Top  < wa.Top)    Top  = wa.Top;
        if (Left + Width  > wa.Right)  Left = wa.Right  - Width;
        if (Top  + Height > wa.Bottom) Top  = wa.Bottom - Height;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files != null && files.Any(f => System.IO.Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = System.Windows.DragDropEffects.None;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            var pdf = files?.FirstOrDefault(f => System.IO.Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(pdf) && System.IO.File.Exists(pdf))
            {
                _ = Vm.LoadDocumentAsync(pdf);
            }
        }
    }

    private void Thumbnail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfPageThumbnailItem thumb)
        {
            Vm.CurrentPageIndex = thumb.PageIndex;
        }
    }

    private void PageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Delta > 0)
                Vm.ZoomInCommand.Execute(null);
            else
                Vm.ZoomOutCommand.Execute(null);

            e.Handled = true;
        }
    }

    private void EditCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WpfPoint pt = e.GetPosition(EditCanvas);

        if (Vm.IsWhiteoutTool)
        {
            _dragStartPoint = pt;
            _isCreatingWhiteout = true;

            Canvas.SetLeft(RubberBand, pt.X);
            Canvas.SetTop(RubberBand, pt.Y);
            RubberBand.Width = 0;
            RubberBand.Height = 0;
            RubberBand.Visibility = Visibility.Visible;

            EditCanvas.CaptureMouse();
            e.Handled = true;
        }
        else if (Vm.IsTextTool)
        {
            // Click to create new text item
            var textItem = new PdfTextItem
            {
                X = pt.X,
                Y = pt.Y,
                Width = 140,
                Height = Math.Max(26, Vm.FontSizePt * 1.5),
                Text = "New Text",
                FontFamily = Vm.SelectedFontFamily,
                FontSizePt = Vm.FontSizePt,
                IsBold = Vm.IsBold,
                IsItalic = Vm.IsItalic,
                TextColorHex = Vm.TextColorHex,
                HasOpaqueBackground = Vm.HasOpaqueBackground
            };

            Vm.AddEditItem(textItem);
            Vm.SelectedTool = PdfEditorTool.Select;
            e.Handled = true;
        }
        else if (Vm.IsPenTool)
        {
            _activeInkItem = new PdfInkItem
            {
                X = 0,
                Y = 0,
                StrokeColorHex = Vm.TextColorHex,
                StrokeThickness = 2.0
            };
            _activeInkItem.Points.Add((pt.X, pt.Y));
            Vm.AddEditItem(_activeInkItem);
            _isDrawingInk = true;
            EditCanvas.CaptureMouse();
            e.Handled = true;
        }
        else if (Vm.IsSelectTool || Vm.IsEditTextTool)
        {
            // Deselect if clicked on empty canvas
            if (e.OriginalSource == EditCanvas)
            {
                Vm.SelectedItem = null;
            }
        }
    }

    private void EditCanvas_MouseMove(object sender, WpfMouseEventArgs e)
    {
        WpfPoint pt = e.GetPosition(EditCanvas);

        if (_isCreatingWhiteout)
        {
            double x = Math.Min(_dragStartPoint.X, pt.X);
            double y = Math.Min(_dragStartPoint.Y, pt.Y);
            double w = Math.Abs(pt.X - _dragStartPoint.X);
            double h = Math.Abs(pt.Y - _dragStartPoint.Y);

            Canvas.SetLeft(RubberBand, x);
            Canvas.SetTop(RubberBand, y);
            RubberBand.Width = w;
            RubberBand.Height = h;
        }
        else if (_isDrawingInk && _activeInkItem != null)
        {
            _activeInkItem.AddPoint(pt.X, pt.Y);
        }
        else if (_isDraggingItem && _activeItem != null)
        {
            double dx = pt.X - _lastMouseCanvasPos.X;
            double dy = pt.Y - _lastMouseCanvasPos.Y;

            _activeItem.X = Math.Max(0, _activeItem.X + dx);
            _activeItem.Y = Math.Max(0, _activeItem.Y + dy);

            _lastMouseCanvasPos = pt;
        }
        else if (_isResizingItem && _activeItem != null)
        {
            double dx = pt.X - _lastMouseCanvasPos.X;
            double dy = pt.Y - _lastMouseCanvasPos.Y;

            _activeItem.Width = Math.Max(10, _activeItem.Width + dx);
            _activeItem.Height = Math.Max(10, _activeItem.Height + dy);

            _lastMouseCanvasPos = pt;
        }
    }

    private void EditCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isCreatingWhiteout)
        {
            _isCreatingWhiteout = false;
            RubberBand.Visibility = Visibility.Collapsed;
            EditCanvas.ReleaseMouseCapture();

            double w = RubberBand.Width;
            double h = RubberBand.Height;

            if (w > 4 && h > 4)
            {
                var whiteout = new PdfWhiteoutItem
                {
                    X = Canvas.GetLeft(RubberBand),
                    Y = Canvas.GetTop(RubberBand),
                    Width = w,
                    Height = h,
                    FillColorHex = "#FFFFFF"
                };

                Vm.AddEditItem(whiteout);
                Vm.SelectedTool = PdfEditorTool.Select;
            }
        }
        else if (_isDrawingInk)
        {
            _isDrawingInk = false;
            if (_activeInkItem != null && _activeInkItem.Points.Count < 2)
            {
                _activeInkItem.AddPoint(_activeInkItem.Points[0].X + 0.5, _activeInkItem.Points[0].Y + 0.5);
            }
            _activeInkItem = null;
            EditCanvas.ReleaseMouseCapture();
            Vm.NotifyEditsChanged();
        }
        else if (_isDraggingItem || _isResizingItem)
        {
            _isDraggingItem = false;
            _isResizingItem = false;
            _activeItem = null;
            EditCanvas.ReleaseMouseCapture();
            Vm.NotifyEditsChanged();
        }
    }

    private void EditItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfEditItem item)
        {
            Vm.SelectedItem = item;

            if (Vm.IsSelectTool)
            {
                _activeItem = item;
                _lastMouseCanvasPos = e.GetPosition(EditCanvas);
                _isDraggingItem = true;
                EditCanvas.CaptureMouse();
                e.Handled = true;
            }
        }
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfEditItem item)
        {
            Vm.SelectedItem = item;
            _activeItem = item;
            _lastMouseCanvasPos = e.GetPosition(EditCanvas);
            _isResizingItem = true;
            EditCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Window_KeyDown(object sender, WpfKeyEventArgs e)
    {
        // Don't intercept typing if user is typing in a TextBox
        bool isTextBoxFocused = FocusManager.GetFocusedElement(this) is System.Windows.Controls.TextBox;

        if (e.Key == Key.F1)
        {
            Vm.SelectedTool = PdfEditorTool.Select;
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            Vm.SelectedTool = PdfEditorTool.Whiteout;
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            Vm.SelectedTool = PdfEditorTool.Text;
            e.Handled = true;
        }
        else if (e.Key == Key.F4)
        {
            Vm.SelectedTool = PdfEditorTool.EditText;
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Vm.SelectedTool = PdfEditorTool.Pen;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !isTextBoxFocused)
        {
            if (Vm.HasSelectedItem)
            {
                Vm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    Vm.OpenPdfCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.S:
                    Vm.SavePdfCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.P:
                    Vm.PrintPdfCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Z:
                    Vm.UndoCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Y:
                    Vm.RedoCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.V:
                    if (!isTextBoxFocused)
                    {
                        Vm.PasteClipboardImageCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
            }
        }
        else if (!isTextBoxFocused && Vm.SelectedItem != null)
        {
            // 1-pixel micro-nudge with arrow keys
            double step = (Keyboard.Modifiers == ModifierKeys.Shift) ? 5.0 : 1.0;
            switch (e.Key)
            {
                case Key.Left:
                    Vm.SelectedItem.X = Math.Max(0, Vm.SelectedItem.X - step);
                    e.Handled = true;
                    break;
                case Key.Right:
                    Vm.SelectedItem.X += step;
                    e.Handled = true;
                    break;
                case Key.Up:
                    Vm.SelectedItem.Y = Math.Max(0, Vm.SelectedItem.Y - step);
                    e.Handled = true;
                    break;
                case Key.Down:
                    Vm.SelectedItem.Y += step;
                    e.Handled = true;
                    break;
            }
        }
    }

    #region Edit Text Overlay Handlers

    private void ExtractedBlock_MouseEnter(object sender, WpfMouseEventArgs e)
    {
        if (sender is Border b)
        {
            // Subtle, sleek cyan outline ONLY on hover
            b.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 0, 188, 212));
            b.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 188, 212));
            b.BorderThickness = new Thickness(1);
        }
    }

    private void ExtractedBlock_MouseLeave(object sender, WpfMouseEventArgs e)
    {
        if (sender is Border b)
        {
            // Fully transparent when not hovered — clean document view
            b.Background = System.Windows.Media.Brushes.Transparent;
            b.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void ExtractedBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfExtractedTextBlock block)
        {
            var textItem = Vm.StartEditingExtractedBlock(block);
            e.Handled = true;

            if (textItem != null)
            {
                // Force immediate visual tree generation so FindVisualChild finds the TextBox
                EditCanvas.UpdateLayout();

                // After layout update, focus the TextBox for this new text item and select all
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
                {
                    var tb = FindVisualChild<System.Windows.Controls.TextBox>(EditCanvas, t => t.DataContext == textItem);
                    if (tb != null)
                    {
                        tb.Focus();
                        tb.SelectAll();
                    }
                });
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && (predicate == null || predicate(typed)))
            {
                return typed;
            }

            var nested = FindVisualChild(child, predicate);
            if (nested != null) return nested;
        }
        return null;
    }

    private void TextItem_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PdfEditItem item)
        {
            Vm.SelectedItem = item;
        }
    }

    private void TextItem_LostFocus(object sender, RoutedEventArgs e)
    {
        // When typing finishes or user clicks away, deselect so the cyan editing border vanishes immediately
        if (sender is FrameworkElement fe && fe.DataContext is PdfEditItem item && Vm.SelectedItem == item)
        {
            Vm.SelectedItem = null;
        }
        Vm.NotifyEditsChanged();
    }

    private void TextItem_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        // Enter (without Shift) or Escape commits edit and clears the selection border
        if ((e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) || e.Key == Key.Escape)
        {
            if (sender is System.Windows.Controls.TextBox tb)
            {
                var be = tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
                be?.UpdateSource();
            }
            Vm.SelectedItem = null;
            Keyboard.ClearFocus();
            Vm.NotifyEditsChanged();
            e.Handled = true;
        }
    }

    private void PdfPasswordTextBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is System.Windows.Controls.TextBox tb)
            {
                var be = tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
                be?.UpdateSource();
            }
            if (Vm.UnlockPdfCommand.CanExecute(null))
            {
                Vm.UnlockPdfCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void PdfPasswordTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PdfPasswordTextBox.IsVisible)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                PdfPasswordTextBox.Focus();
                PdfPasswordTextBox.SelectAll();
            });
        }
    }

    #endregion
}
