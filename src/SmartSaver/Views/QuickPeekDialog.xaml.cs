using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class QuickPeekDialog : Window
{
    private readonly QuickPeekViewModel _vm;
    private readonly DateTime _openedTimestamp;

    // Pan/drag state
    private bool _isPanning;
    private System.Windows.Point _panStartPoint;
    private double _panStartOffsetH;
    private double _panStartOffsetV;

    public int ResultRotation => _vm.PageRotation;
    public string FilePath => _vm.FilePath;

    public QuickPeekDialog(string filePath, int initialPageIndex = 0, int initialRotation = 0)
    {
        InitializeComponent();
        _openedTimestamp = DateTime.UtcNow;
        _vm = new QuickPeekViewModel(filePath, initialPageIndex, initialRotation);
        _vm.RequestClose = () => Close();
        _vm.RequestCompress = path =>
        {
            var dialog = new FileCompressDialog(path) { Owner = this };
            dialog.Show();
        };
        _vm.RequestStack = path =>
        {
            var stacker = new DocumentStackerWindow(path) { Owner = this };
            stacker.Show();
        };
        _vm.RequestGovtCardPrint = path =>
        {
            Close();
            App.CurrentApp?.OpenGovtCardPrintWithFile(path);
        };
        DataContext = _vm;
        Topmost = true;

        // Update cursor when zoom level changes (show grab cursor when zoomed in)
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(QuickPeekViewModel.ZoomLevel))
            {
                UpdatePanCursor();
            }
            else if (args.PropertyName == nameof(QuickPeekViewModel.SelectedPdfPage) ||
                     args.PropertyName == nameof(QuickPeekViewModel.PageRotation))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdatePdfLayout();
                    PdfScrollViewer.ScrollToVerticalOffset(0);
                    PdfScrollViewer.ScrollToHorizontalOffset(0);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else if (args.PropertyName == nameof(QuickPeekViewModel.ImageSource))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateImageLayout();
                    ImageScrollViewer.ScrollToVerticalOffset(0);
                    ImageScrollViewer.ScrollToHorizontalOffset(0);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };

        SizeChanged += (_, _) =>
        {
            UpdatePdfLayout();
            UpdateImageLayout();
        };

        PdfScrollViewer.SizeChanged += (_, _) => UpdatePdfLayout();
        ImageScrollViewer.SizeChanged += (_, _) => UpdateImageLayout();

        Loaded += (_, _) =>
        {
            Helpers.WindowActivationHelper.BringToForeground(this, keepTopmost: true);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdatePdfLayout();
                UpdateImageLayout();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private void UpdatePdfLayout()
    {
        if (PdfScrollViewer == null || PdfPageImage == null || _vm.SelectedPdfPage == null) return;

        double vWidth = PdfScrollViewer.ViewportWidth > 50 ? PdfScrollViewer.ViewportWidth : PdfScrollViewer.ActualWidth;
        double vHeight = PdfScrollViewer.ViewportHeight > 50 ? PdfScrollViewer.ViewportHeight : PdfScrollViewer.ActualHeight;

        if (vWidth > 50 && vHeight > 50)
        {
            // Deduct 36px for clean 16px margins on all sides
            double availableWidth = Math.Max(200, vWidth - 36);
            double availableHeight = Math.Max(200, vHeight - 36);
            double bmpWidth = _vm.SelectedPdfPage.Width;
            double bmpHeight = _vm.SelectedPdfPage.Height;
            bool isRotated90 = (_vm.PageRotation % 180 != 0);

            // Calculate effective dimensions after rotation
            double effectiveWidth = isRotated90 ? bmpHeight : bmpWidth;
            double effectiveHeight = isRotated90 ? bmpWidth : bmpHeight;

            if (effectiveWidth <= 0 || effectiveHeight <= 0)
            {
                PdfPageImage.Width = availableWidth;
                PdfPageImage.Height = double.NaN;
                return;
            }

            // Fit both width and height cleanly without distortion or stretch
            double scaleW = availableWidth / effectiveWidth;
            double scaleH = availableHeight / effectiveHeight;
            double fitScale = Math.Min(scaleW, scaleH);

            // Assign both width and height proportionally to guarantee 100% aspect ratio preservation
            PdfPageImage.Width = bmpWidth * fitScale;
            PdfPageImage.Height = bmpHeight * fitScale;
        }
    }

    private void UpdateImageLayout()
    {
        if (ImageScrollViewer == null || ImageViewerImage == null || _vm.ImageSource == null) return;
        if (ImageScrollViewer.ViewportWidth > 50)
        {
            double availableWidth = Math.Max(200, ImageScrollViewer.ViewportWidth - 32);
            double naturalWidth = _vm.ImageSource.Width;
            if (naturalWidth > availableWidth || naturalWidth <= 0)
            {
                ImageViewerImage.Width = availableWidth;
            }
            else
            {
                ImageViewerImage.Width = naturalWidth;
            }
            ImageViewerImage.Height = double.NaN;
        }
    }

    public async Task NavigateToFile(string filePath)
    {
        Helpers.WindowActivationHelper.BringToForeground(this, keepTopmost: true);
        await _vm.NavigateToFileAsync(filePath);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // --- Pan/Drag Support ---

    private void UpdatePanCursor()
    {
        var cursor = _vm.ZoomLevel > 1.05 ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        PdfScrollViewer.Cursor = cursor;
        ImageScrollViewer.Cursor = cursor;
    }

    private void PanScrollViewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.ZoomLevel <= 1.05) return; // Only pan when zoomed in

        if (sender is ScrollViewer sv)
        {
            _isPanning = true;
            _panStartPoint = e.GetPosition(sv);
            _panStartOffsetH = sv.HorizontalOffset;
            _panStartOffsetV = sv.VerticalOffset;
            sv.Cursor = System.Windows.Input.Cursors.ScrollAll;
            sv.CaptureMouse();
            e.Handled = true;
        }
    }

    private void PanScrollViewer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning) return;

        if (sender is ScrollViewer sv)
        {
            var currentPos = e.GetPosition(sv);
            double deltaX = _panStartPoint.X - currentPos.X;
            double deltaY = _panStartPoint.Y - currentPos.Y;

            sv.ScrollToHorizontalOffset(_panStartOffsetH + deltaX);
            sv.ScrollToVerticalOffset(_panStartOffsetV + deltaY);
            e.Handled = true;
        }
    }

    private void PanScrollViewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;

        _isPanning = false;
        if (sender is ScrollViewer sv)
        {
            sv.ReleaseMouseCapture();
            sv.Cursor = _vm.ZoomLevel > 1.05 ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
            e.Handled = true;
        }
    }

    // --- Zoom via Ctrl+Scroll ---

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Delta > 0)
            {
                _vm.ZoomLevel += 0.15;
            }
            else if (e.Delta < 0)
            {
                _vm.ZoomLevel -= 0.15;
            }
            e.Handled = true;
        }
    }

    // --- Keyboard Shortcuts ---

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // If the user is typing in a TextBox (e.g. entering PDF password), do not intercept text navigation keys
        if (e.OriginalSource is System.Windows.Controls.TextBox)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            return;
        }

        // --- Zoom Shortcuts: Ctrl + Plus, Ctrl + Minus, Ctrl + 0 ---
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                _vm.ZoomLevel += 0.15;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                _vm.ZoomLevel -= 0.15;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            {
                _vm.ZoomLevel = 1.0;
                e.Handled = true;
                return;
            }

        }

        // --- PDF Page Navigation: Right Arrow (Next Page) & Left Arrow (Prev Page) ---
        // Right and Left arrow ONLY turn pages in a multi-page PDF, NEVER switch files.
        if (e.Key == Key.Right)
        {
            if (_vm.IsPdf && _vm.TotalPagesCount > 1 && _vm.CanGoNextPage)
            {
                _vm.CurrentPageIndex++;
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            if (_vm.IsPdf && _vm.TotalPagesCount > 1 && _vm.CanGoPrevPage)
            {
                _vm.CurrentPageIndex--;
            }
            e.Handled = true;
            return;
        }

        // --- File Navigation: Next File (Down Arrow, N, Alt+Down) ---
        if (e.Key == Key.Down || e.Key == Key.N ||
            (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.Down))
        {
            if (_vm.CanGoNextFile)
            {
                _vm.NavigateToNextFile();
            }
            e.Handled = true;
            return;
        }

        // --- File Navigation: Previous File (Up Arrow, P, Alt+Up) ---
        if (e.Key == Key.Up || e.Key == Key.P ||
            (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.Up))
        {
            if (_vm.CanGoPrevFile)
            {
                _vm.NavigateToPrevFile();
            }
            e.Handled = true;
            return;
        }

        // --- Page Navigation: PageDown / PageUp ---
        if (e.Key == Key.PageDown)
        {
            // If in a multi-page PDF and not on the last page, advance page
            if (_vm.IsPdf && _vm.CanGoNextPage)
            {
                _vm.CurrentPageIndex++;
                e.Handled = true;
                return;
            }
            // Otherwise advance to next file
            if (_vm.CanGoNextFile)
            {
                _vm.NavigateToNextFile();
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageUp)
        {
            // If in a multi-page PDF and not on the first page, go to previous page
            if (_vm.IsPdf && _vm.CanGoPrevPage)
            {
                _vm.CurrentPageIndex--;
                e.Handled = true;
                return;
            }
            // Otherwise go to previous file
            if (_vm.CanGoPrevFile)
            {
                _vm.NavigateToPrevFile();
            }
            e.Handled = true;
            return;
        }

        // --- Dialog Dismissal: Escape or Spacebar ---
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space)
        {
            // Debounce Spacebar closing: ignore Spacebar if pressed within 350ms of dialog creation
            if ((DateTime.UtcNow - _openedTimestamp).TotalMilliseconds > 350)
            {
                Close();
            }
            e.Handled = true;
            return;
        }
    }
}
