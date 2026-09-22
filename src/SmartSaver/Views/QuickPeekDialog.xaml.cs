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
        };

        Loaded += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                SetForegroundWindow(hwnd);
            }
            catch { }
            Activate();
            Focus();
        };
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

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
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

        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Space)
        {
            // Debounce Spacebar closing: ignore Spacebar if pressed within 350ms of dialog creation
            if ((DateTime.UtcNow - _openedTimestamp).TotalMilliseconds > 350)
            {
                Close();
            }
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Left && _vm.CanGoPrevPage)
        {
            _vm.CurrentPageIndex--;
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Right && _vm.CanGoNextPage)
        {
            _vm.CurrentPageIndex++;
            e.Handled = true;
        }
    }
}
