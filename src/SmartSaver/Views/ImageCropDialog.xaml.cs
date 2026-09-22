using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Serilog;
using SmartSaver.Services;
using WpfApplication = System.Windows.Application;
using WpfPoint = System.Windows.Point;
using WpfRectangleGeometry = System.Windows.Media.RectangleGeometry;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Cursors = System.Windows.Input.Cursors;
using Cursor = System.Windows.Input.Cursor;

namespace SmartSaver.Views;

public partial class ImageCropDialog : Window
{
    private string _currentImagePath;
    private BitmapImage? _bitmap;
    private double _origWidth;
    private double _origHeight;

    // Display scale & offset on canvas
    private double _dispScale = 1.0;
    private double _dispX = 0;
    private double _dispY = 0;
    private double _dispW = 0;
    private double _dispH = 0;

    // Crop box in canvas coordinates
    private double _cropX = 0;
    private double _cropY = 0;
    private double _cropW = 0;
    private double _cropH = 0;

    // Dragging state for moving crop box
    private bool _isDraggingBox = false;
    private WpfPoint _dragStartPoint;
    private double _dragStartCropX;
    private double _dragStartCropY;

    // 4-Corner Quad Deskew State
    private bool _isQuadMode = false;
    private WpfPoint _quadTL;
    private WpfPoint _quadTR;
    private WpfPoint _quadBR;
    private WpfPoint _quadBL;

    private readonly bool _isInitialized = false;

    public string? CroppedImagePath { get; private set; }

    public ImageCropDialog(string imagePath, bool startInQuadMode = false, string? initialPreset = null)
    {
        InitializeComponent();
        _currentImagePath = imagePath;
        _isQuadMode = startInQuadMode;
        _isInitialized = true;

        Loaded += (_, _) =>
        {
            try
            {
                if (_isQuadMode && RbModeQuad != null)
                {
                    RbModeQuad.IsChecked = true;
                }
                else if (string.Equals(initialPreset, "passport", StringComparison.OrdinalIgnoreCase) && RbPassport != null)
                {
                    RbPassport.IsChecked = true;
                }
                else if (string.Equals(initialPreset, "stamp", StringComparison.OrdinalIgnoreCase) && RbStamp != null)
                {
                    RbStamp.IsChecked = true;
                }
                else if (string.Equals(initialPreset, "idcard", StringComparison.OrdinalIgnoreCase) && RbIdCard != null)
                {
                    RbIdCard.IsChecked = true;
                }

                LoadImage();
                UpdateModeVisibility();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ImageCropDialog Loaded event error");
            }
        };
    }

    public static string? ShowCropDialog(string imagePath, Window? owner = null, bool startInQuadMode = false, string? initialPreset = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;

        var dlg = new ImageCropDialog(imagePath, startInQuadMode, initialPreset);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        else if (WpfApplication.Current?.MainWindow != null && WpfApplication.Current.MainWindow.IsVisible)
        {
            dlg.Owner = WpfApplication.Current.MainWindow;
        }

        if (dlg.ShowDialog() == true)
        {
            return dlg.CroppedImagePath;
        }
        return null;
    }

    private void LoadImage(double? preserveNormX = null, double? preserveNormY = null, double? preserveNormW = null, double? preserveNormH = null)
    {
        try
        {
            if (!File.Exists(_currentImagePath)) return;

            var bi = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmapImage(_currentImagePath);

            _bitmap = bi;
            _origWidth = bi.PixelWidth;
            _origHeight = bi.PixelHeight;

            ImgSource.Source = bi;
            TxtOriginalDimensions.Text = $"Original: {_origWidth:F0} × {_origHeight:F0}";

            UpdateCanvasLayout();

            if (preserveNormX.HasValue && preserveNormY.HasValue && preserveNormW.HasValue && preserveNormH.HasValue && _dispW > 0 && _dispH > 0)
            {
                _cropX = _dispX + preserveNormX.Value * _dispW;
                _cropY = _dispY + preserveNormY.Value * _dispH;
                _cropW = preserveNormW.Value * _dispW;
                _cropH = preserveNormH.Value * _dispH;
                ClampCropBox();
                UpdateOverlayAndHandles();
            }
            else
            {
                // Default initial crop box: 80% centered with active aspect ratio preset
                ResetToDefaultCrop();
                InitQuadPoints();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load image into crop dialog");
            TxtStatus.Text = $"Error: {ex.Message}";
        }
    }

    private void CanvasBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCanvasLayout();
    }

    private void UpdateCanvasLayout()
    {
        if (_bitmap == null || CropCanvas.ActualWidth <= 0 || CropCanvas.ActualHeight <= 0) return;

        double canvasW = CropCanvas.ActualWidth;
        double canvasH = CropCanvas.ActualHeight;

        double scaleX = (canvasW - 20) / _origWidth;
        double scaleY = (canvasH - 20) / _origHeight;
        _dispScale = Math.Min(scaleX, scaleY);

        _dispW = _origWidth * _dispScale;
        _dispH = _origHeight * _dispScale;
        _dispX = (canvasW - _dispW) / 2.0;
        _dispY = (canvasH - _dispH) / 2.0;

        Canvas.SetLeft(ImgSource, _dispX);
        Canvas.SetTop(ImgSource, _dispY);
        ImgSource.Width = _dispW;
        ImgSource.Height = _dispH;

        // Ensure crop box and quad are within bounds
        ClampCropBox();
        if (_isQuadMode)
        {
            UpdateQuadOverlay();
        }
        else
        {
            UpdateOverlayAndHandles();
        }
    }

    private double GetActiveAspectRatio()
    {
        if (RbPassport?.IsChecked == true)
        {
            bool isLandscape = _cropW >= _cropH || (_cropW == 0 && _dispW >= _dispH);
            return isLandscape ? (4.50 / 3.50) : (3.50 / 4.50);
        }
        if (RbStamp?.IsChecked == true)
        {
            bool isLandscape = _cropW >= _cropH || (_cropW == 0 && _dispW >= _dispH);
            return isLandscape ? (3.00 / 2.50) : (2.50 / 3.00);
        }
        if (RbIdCard?.IsChecked == true)
        {
            bool isLandscape = _cropW >= _cropH || (_cropW == 0 && _dispW >= _dispH);
            return isLandscape ? (8.50 / 5.50) : (5.50 / 8.50);
        }
        if (RbRation?.IsChecked == true)
        {
            bool isLandscape = _cropW >= _cropH || (_cropW == 0 && _dispW >= _dispH);
            return isLandscape ? (8.00 / 5.40) : (5.40 / 8.00);
        }
        return 0.0; // Freeform
    }

    public void SnapToAspectRatio(double targetRatio)
    {
        if (targetRatio <= 0 || _dispW <= 0 || _dispH <= 0) return;

        double cx = _cropX + _cropW / 2.0;
        double cy = _cropY + _cropH / 2.0;

        double newW = _cropW;
        double newH = newW / targetRatio;

        if (newH > _dispH)
        {
            newH = _dispH;
            newW = newH * targetRatio;
        }
        if (newW > _dispW)
        {
            newW = _dispW;
            newH = newW / targetRatio;
        }

        _cropW = newW;
        _cropH = newH;
        _cropX = cx - _cropW / 2.0;
        _cropY = cy - _cropH / 2.0;

        ClampCropBox();
        UpdateOverlayAndHandles();
    }

    private void SnapToPassportRatio_Click(object sender, RoutedEventArgs e)
    {
        if (RbPassport != null) RbPassport.IsChecked = true;
        bool isLandscape = _cropW >= _cropH || (_cropW == 0 && _dispW >= _dispH);
        double ratio = isLandscape ? (4.50 / 3.50) : (3.50 / 4.50);
        SnapToAspectRatio(ratio);
        if (TxtStatus != null) TxtStatus.Text = $"Snapped to {(isLandscape ? "Landscape" : "Portrait")} Passport Photo ratio (3.5 × 4.5 cm / {ratio:F2})";
    }

    private void SnapToIdRatio_Click(object sender, RoutedEventArgs e)
    {
        if (RbIdCard != null) RbIdCard.IsChecked = true;
        bool isLandscape = _cropW >= _cropH || (_cropW == 0 && _dispW >= _dispH);
        double ratio = isLandscape ? (8.50 / 5.50) : (5.50 / 8.50);
        SnapToAspectRatio(ratio);
        if (TxtStatus != null) TxtStatus.Text = $"Snapped to {(isLandscape ? "Landscape" : "Portrait")} ID Card ratio (8.5 × 5.5 cm / {ratio:F2})";
    }

    private void ResetToDefaultCrop()
    {
        if (_dispW <= 0 || _dispH <= 0) return;

        double targetRatio = GetActiveAspectRatio();
        if (targetRatio > 0)
        {
            _cropW = _dispW * 0.85;
            _cropH = _cropW / targetRatio;
            if (_cropH > _dispH * 0.85)
            {
                _cropH = _dispH * 0.85;
                _cropW = _cropH * targetRatio;
            }
        }
        else
        {
            _cropW = _dispW * 0.85;
            _cropH = _dispH * 0.85;
        }

        _cropX = _dispX + (_dispW - _cropW) / 2.0;
        _cropY = _dispY + (_dispH - _cropH) / 2.0;

        UpdateOverlayAndHandles();
    }

    private void ClampCropBox()
    {
        _cropW = Math.Max(20, Math.Min(_dispW, _cropW));
        _cropH = Math.Max(20, Math.Min(_dispH, _cropH));
        _cropX = Math.Max(_dispX, Math.Min(_dispX + _dispW - _cropW, _cropX));
        _cropY = Math.Max(_dispY, Math.Min(_dispY + _dispH - _cropH, _cropY));
    }

    private void UpdateOverlayAndHandles()
    {
        if (CropCanvas == null || CropBox == null || DarkOverlayPath == null || CropCanvas.ActualWidth <= 0 || CropCanvas.ActualHeight <= 0) return;

        // Position crop box rectangle
        Canvas.SetLeft(CropBox, _cropX);
        Canvas.SetTop(CropBox, _cropY);
        CropBox.Width = _cropW;
        CropBox.Height = _cropH;

        // Position 8 handles
        Canvas.SetLeft(ThumbTL, _cropX);
        Canvas.SetTop(ThumbTL, _cropY);

        Canvas.SetLeft(ThumbT, _cropX + _cropW / 2.0);
        Canvas.SetTop(ThumbT, _cropY);

        Canvas.SetLeft(ThumbTR, _cropX + _cropW);
        Canvas.SetTop(ThumbTR, _cropY);

        Canvas.SetLeft(ThumbR, _cropX + _cropW);
        Canvas.SetTop(ThumbR, _cropY + _cropH / 2.0);

        Canvas.SetLeft(ThumbBR, _cropX + _cropW);
        Canvas.SetTop(ThumbBR, _cropY + _cropH);

        Canvas.SetLeft(ThumbB, _cropX + _cropW / 2.0);
        Canvas.SetTop(ThumbB, _cropY + _cropH);

        Canvas.SetLeft(ThumbBL, _cropX);
        Canvas.SetTop(ThumbBL, _cropY + _cropH);

        Canvas.SetLeft(ThumbL, _cropX);
        Canvas.SetTop(ThumbL, _cropY + _cropH / 2.0);

        // Update Dimmed Overlay (Full canvas minus crop box)
        var fullRect = new WpfRectangleGeometry(new WpfRect(0, 0, CropCanvas.ActualWidth, CropCanvas.ActualHeight));
        var cropRect = new WpfRectangleGeometry(new WpfRect(_cropX, _cropY, _cropW, _cropH));
        var combined = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, cropRect);
        DarkOverlayPath.Data = combined;

        // Update Dimension Readouts
        if (_dispScale > 0)
        {
            double pixelW = _cropW / _dispScale;
            double pixelH = _cropH / _dispScale;
            double ratio = pixelH > 0 ? (pixelW / pixelH) : 1.0;
            TxtCropDimensions.Text = $"Crop: {pixelW:F0} × {pixelH:F0} (Ratio: {ratio:F2})";
        }
    }

    #region Mouse Drag for Moving Crop Box
    private void CropBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingBox = true;
        _dragStartPoint = e.GetPosition(CropCanvas);
        _dragStartCropX = _cropX;
        _dragStartCropY = _cropY;
        CropBox.CaptureMouse();
        e.Handled = true;
    }

    private void CropBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingBox && e.LeftButton == MouseButtonState.Pressed)
        {
            var curPoint = e.GetPosition(CropCanvas);
            double dx = curPoint.X - _dragStartPoint.X;
            double dy = curPoint.Y - _dragStartPoint.Y;

            _cropX = _dragStartCropX + dx;
            _cropY = _dragStartCropY + dy;

            ClampCropBox();
            UpdateOverlayAndHandles();
            e.Handled = true;
        }
    }

    private void CropBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingBox)
        {
            _isDraggingBox = false;
            CropBox.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingBox && e.LeftButton == MouseButtonState.Pressed)
        {
            var curPoint = e.GetPosition(CropCanvas);
            double dx = curPoint.X - _dragStartPoint.X;
            double dy = curPoint.Y - _dragStartPoint.Y;

            _cropX = _dragStartCropX + dx;
            _cropY = _dragStartCropY + dy;

            ClampCropBox();
            UpdateOverlayAndHandles();
        }
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingBox)
        {
            _isDraggingBox = false;
            CropBox.ReleaseMouseCapture();
        }
    }

    private void CropCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // If clicked outside crop box, center crop box to click point
        var pt = e.GetPosition(CropCanvas);
        if (pt.X >= _dispX && pt.X <= _dispX + _dispW && pt.Y >= _dispY && pt.Y <= _dispY + _dispH)
        {
            _cropX = pt.X - _cropW / 2.0;
            _cropY = pt.Y - _cropH / 2.0;
            ClampCropBox();
            UpdateOverlayAndHandles();
        }
    }
    #endregion

    #region Thumb Drag Resizing Handles
    // EDGE HANDLES: Every edge handle moves ONLY that edge! Never modifies perpendicular dimensions.
    private void ThumbL_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double minX = _dispX;
        double maxX = _cropX + _cropW - 20; // 20px min width
        double newX = Math.Max(minX, Math.Min(maxX, _cropX + e.HorizontalChange));
        _cropW = (_cropX + _cropW) - newX;
        _cropX = newX;
        UpdateOverlayAndHandles();
    }

    private void ThumbR_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double maxW = (_dispX + _dispW) - _cropX;
        _cropW = Math.Max(20, Math.Min(maxW, _cropW + e.HorizontalChange));
        UpdateOverlayAndHandles();
    }

    private void ThumbT_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double minY = _dispY;
        double maxY = _cropY + _cropH - 20; // 20px min height
        double newY = Math.Max(minY, Math.Min(maxY, _cropY + e.VerticalChange));
        _cropH = (_cropY + _cropH) - newY;
        _cropY = newY;
        UpdateOverlayAndHandles();
    }

    private void ThumbB_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double maxH = (_dispY + _dispH) - _cropY;
        _cropH = Math.Max(20, Math.Min(maxH, _cropH + e.VerticalChange));
        UpdateOverlayAndHandles();
    }

    // CORNER HANDLES: Resizes both axes smoothly.
    private void ThumbBR_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double targetRatio = GetActiveAspectRatio();
        double maxW = (_dispX + _dispW) - _cropX;
        double maxH = (_dispY + _dispH) - _cropY;

        if (targetRatio > 0)
        {
            double deltaW = (e.HorizontalChange * targetRatio + e.VerticalChange) / (targetRatio * targetRatio + 1.0) * targetRatio;
            double newW = Math.Max(20, Math.Min(maxW, _cropW + deltaW));
            double newH = newW / targetRatio;
            if (newH > maxH)
            {
                newH = maxH;
                newW = newH * targetRatio;
            }
            _cropW = newW;
            _cropH = newH;
        }
        else
        {
            _cropW = Math.Max(20, Math.Min(maxW, _cropW + e.HorizontalChange));
            _cropH = Math.Max(20, Math.Min(maxH, _cropH + e.VerticalChange));
        }
        UpdateOverlayAndHandles();
    }

    private void ThumbBL_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double targetRatio = GetActiveAspectRatio();
        double fixedRight = _cropX + _cropW;
        double maxW = fixedRight - _dispX;
        double maxH = (_dispY + _dispH) - _cropY;

        if (targetRatio > 0)
        {
            double deltaW = (-e.HorizontalChange * targetRatio + e.VerticalChange) / (targetRatio * targetRatio + 1.0) * targetRatio;
            double newW = Math.Max(20, Math.Min(maxW, _cropW + deltaW));
            double newH = newW / targetRatio;
            if (newH > maxH)
            {
                newH = maxH;
                newW = newH * targetRatio;
            }
            _cropX = fixedRight - newW;
            _cropW = newW;
            _cropH = newH;
        }
        else
        {
            double newW = Math.Max(20, Math.Min(maxW, _cropW - e.HorizontalChange));
            _cropX = fixedRight - newW;
            _cropW = newW;
            _cropH = Math.Max(20, Math.Min(maxH, _cropH + e.VerticalChange));
        }
        UpdateOverlayAndHandles();
    }

    private void ThumbTL_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double targetRatio = GetActiveAspectRatio();
        double fixedRight = _cropX + _cropW;
        double fixedBottom = _cropY + _cropH;
        double maxW = fixedRight - _dispX;
        double maxH = fixedBottom - _dispY;

        if (targetRatio > 0)
        {
            double deltaW = (-e.HorizontalChange * targetRatio - e.VerticalChange) / (targetRatio * targetRatio + 1.0) * targetRatio;
            double newW = Math.Max(20, Math.Min(maxW, _cropW + deltaW));
            double newH = newW / targetRatio;
            if (newH > maxH)
            {
                newH = maxH;
                newW = newH * targetRatio;
            }
            _cropX = fixedRight - newW;
            _cropY = fixedBottom - newH;
            _cropW = newW;
            _cropH = newH;
        }
        else
        {
            double newW = Math.Max(20, Math.Min(maxW, _cropW - e.HorizontalChange));
            double newH = Math.Max(20, Math.Min(maxH, _cropH - e.VerticalChange));
            _cropX = fixedRight - newW;
            _cropY = fixedBottom - newH;
            _cropW = newW;
            _cropH = newH;
        }
        UpdateOverlayAndHandles();
    }

    private void ThumbTR_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double targetRatio = GetActiveAspectRatio();
        double fixedBottom = _cropY + _cropH;
        double maxW = (_dispX + _dispW) - _cropX;
        double maxH = fixedBottom - _dispY;

        if (targetRatio > 0)
        {
            double deltaW = (e.HorizontalChange * targetRatio - e.VerticalChange) / (targetRatio * targetRatio + 1.0) * targetRatio;
            double newW = Math.Max(20, Math.Min(maxW, _cropW + deltaW));
            double newH = newW / targetRatio;
            if (newH > maxH)
            {
                newH = maxH;
                newW = newH * targetRatio;
            }
            _cropY = fixedBottom - newH;
            _cropW = newW;
            _cropH = newH;
        }
        else
        {
            double newW = Math.Max(20, Math.Min(maxW, _cropW + e.HorizontalChange));
            double newH = Math.Max(20, Math.Min(maxH, _cropH - e.VerticalChange));
            _cropY = fixedBottom - newH;
            _cropW = newW;
            _cropH = newH;
        }
        UpdateOverlayAndHandles();
    }
    #endregion

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || CropBox == null) return;
        if (_dispScale > 0)
        {
            double targetRatio = GetActiveAspectRatio();
            if (targetRatio > 0)
            {
                _cropH = _cropW / targetRatio;
                ClampCropBox();
                UpdateOverlayAndHandles();
            }
        }
    }

    private async void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        TxtStatus.Text = "Detecting ID card edges on-device...";
        try
        {
            var bounds = await ImageCropService.Instance.DetectCardBoundsAsync(_currentImagePath);
            if (bounds.HasValue)
            {
                var r = bounds.Value;
                _cropX = _dispX + r.X * _dispScale;
                _cropY = _dispY + r.Y * _dispScale;
                _cropW = r.Width * _dispScale;
                _cropH = r.Height * _dispScale;

                ClampCropBox();
                UpdateOverlayAndHandles();
                TxtStatus.Text = "✅ Card detected! Handles snapped to edges.";
            }
            else
            {
                TxtStatus.Text = "Could not detect distinct card edges; manual adjustment ready.";
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Detection failed: {ex.Message}";
        }
    }

    private async void Rotate90_Click(object sender, RoutedEventArgs e)
    {
        if (TxtStatus != null) TxtStatus.Text = "Rotating photo 90° CW...";
        try
        {
            // Calculate current normalized crop coordinates relative to displayed image
            double normX = Math.Max(0.0, Math.Min(1.0, (_cropX - _dispX) / (_dispW > 0 ? _dispW : 1.0)));
            double normY = Math.Max(0.0, Math.Min(1.0, (_cropY - _dispY) / (_dispH > 0 ? _dispH : 1.0)));
            double normW = Math.Max(0.05, Math.Min(1.0, _cropW / (_dispW > 0 ? _dispW : 1.0)));
            double normH = Math.Max(0.05, Math.Min(1.0, _cropH / (_dispH > 0 ? _dispH : 1.0)));

            // Geometrically rotate crop rectangle 90° Clockwise:
            double newNormX = Math.Max(0.0, 1.0 - (normY + normH));
            double newNormY = normX;
            double newNormW = normH;
            double newNormH = normW;

            // Also rotate Quad pins if in Quad mode
            (double x, double y) rTL = (0, 0), rTR = (0, 0), rBR = (0, 0), rBL = (0, 0);
            bool hasQuad = _isQuadMode && _dispW > 0 && _dispH > 0;
            if (hasQuad)
            {
                Func<WpfPoint, (double x, double y)> toN = pt => (Math.Clamp((pt.X - _dispX) / _dispW, 0.0, 1.0), Math.Clamp((pt.Y - _dispY) / _dispH, 0.0, 1.0));
                var nTL = toN(_quadTL);
                var nTR = toN(_quadTR);
                var nBR = toN(_quadBR);
                var nBL = toN(_quadBL);
                // 90° CW: (x, y) -> (1 - y, x). Old BL becomes new TL, TL becomes TR, TR becomes BR, BR becomes BL
                rTL = (1.0 - nBL.y, nBL.x);
                rTR = (1.0 - nTL.y, nTL.x);
                rBR = (1.0 - nTR.y, nTR.x);
                rBL = (1.0 - nBR.y, nBR.x);
            }

            string? rotated = await ImageCropService.Instance.RotateImage90Async(_currentImagePath, clockwise: true);
            if (!string.IsNullOrEmpty(rotated) && File.Exists(rotated))
            {
                _currentImagePath = rotated;
                LoadImage(newNormX, newNormY, newNormW, newNormH);
                if (hasQuad && _dispW > 0 && _dispH > 0)
                {
                    _quadTL = new WpfPoint(_dispX + rTL.x * _dispW, _dispY + rTL.y * _dispH);
                    _quadTR = new WpfPoint(_dispX + rTR.x * _dispW, _dispY + rTR.y * _dispH);
                    _quadBR = new WpfPoint(_dispX + rBR.x * _dispW, _dispY + rBR.y * _dispH);
                    _quadBL = new WpfPoint(_dispX + rBL.x * _dispW, _dispY + rBL.y * _dispH);
                    UpdateQuadOverlay();
                }
                if (TxtStatus != null) TxtStatus.Text = "Photo rotated 90° clockwise. Crop box preserved.";
            }
        }
        catch (Exception ex)
        {
            if (TxtStatus != null) TxtStatus.Text = $"Rotate failed: {ex.Message}";
        }
    }

    private async void RotateCCW_Click(object sender, RoutedEventArgs e)
    {
        if (TxtStatus != null) TxtStatus.Text = "Rotating photo 90° CCW...";
        try
        {
            // Calculate current normalized crop coordinates
            double normX = Math.Max(0.0, Math.Min(1.0, (_cropX - _dispX) / (_dispW > 0 ? _dispW : 1.0)));
            double normY = Math.Max(0.0, Math.Min(1.0, (_cropY - _dispY) / (_dispH > 0 ? _dispH : 1.0)));
            double normW = Math.Max(0.05, Math.Min(1.0, _cropW / (_dispW > 0 ? _dispW : 1.0)));
            double normH = Math.Max(0.05, Math.Min(1.0, _cropH / (_dispH > 0 ? _dispH : 1.0)));

            // Geometrically rotate crop rectangle 90° Counter-Clockwise:
            double newNormX = normY;
            double newNormY = Math.Max(0.0, 1.0 - (normX + normW));
            double newNormW = normH;
            double newNormH = normW;

            // Also rotate Quad pins if in Quad mode
            (double x, double y) rTL = (0, 0), rTR = (0, 0), rBR = (0, 0), rBL = (0, 0);
            bool hasQuad = _isQuadMode && _dispW > 0 && _dispH > 0;
            if (hasQuad)
            {
                Func<WpfPoint, (double x, double y)> toN = pt => (Math.Clamp((pt.X - _dispX) / _dispW, 0.0, 1.0), Math.Clamp((pt.Y - _dispY) / _dispH, 0.0, 1.0));
                var nTL = toN(_quadTL);
                var nTR = toN(_quadTR);
                var nBR = toN(_quadBR);
                var nBL = toN(_quadBL);
                // 90° CCW: (x, y) -> (y, 1 - x). Old TR becomes new TL, BR becomes TR, BL becomes BR, TL becomes BL
                rTL = (nTR.y, 1.0 - nTR.x);
                rTR = (nBR.y, 1.0 - nBR.x);
                rBR = (nBL.y, 1.0 - nBL.x);
                rBL = (nTL.y, 1.0 - nTL.x);
            }

            string? rotated = await ImageCropService.Instance.RotateImage90Async(_currentImagePath, clockwise: false);
            if (!string.IsNullOrEmpty(rotated) && File.Exists(rotated))
            {
                _currentImagePath = rotated;
                LoadImage(newNormX, newNormY, newNormW, newNormH);
                if (hasQuad && _dispW > 0 && _dispH > 0)
                {
                    _quadTL = new WpfPoint(_dispX + rTL.x * _dispW, _dispY + rTL.y * _dispH);
                    _quadTR = new WpfPoint(_dispX + rTR.x * _dispW, _dispY + rTR.y * _dispH);
                    _quadBR = new WpfPoint(_dispX + rBR.x * _dispW, _dispY + rBR.y * _dispH);
                    _quadBL = new WpfPoint(_dispX + rBL.x * _dispW, _dispY + rBL.y * _dispH);
                    UpdateQuadOverlay();
                }
                if (TxtStatus != null) TxtStatus.Text = "Photo rotated 90° counter-clockwise. Crop box preserved.";
            }
        }
        catch (Exception ex)
        {
            if (TxtStatus != null) TxtStatus.Text = $"Rotate failed: {ex.Message}";
        }
    }

    private void ResetToFull_Click(object sender, RoutedEventArgs e)
    {
        _cropX = _dispX;
        _cropY = _dispY;
        _cropW = _dispW;
        _cropH = _dispH;
        UpdateOverlayAndHandles();
        InitQuadPoints();
        if (_isQuadMode)
        {
            UpdateQuadOverlay();
        }
        TxtStatus.Text = "Reset to full photo.";
    }

    #region 4-Corner Quad Deskew / WhatsApp Doc Fixer
    private void InitQuadPoints()
    {
        if (_dispW <= 0 || _dispH <= 0) return;
        double insetX = _dispW * 0.08;
        double insetY = _dispH * 0.08;
        _quadTL = new WpfPoint(_dispX + insetX, _dispY + insetY);
        _quadTR = new WpfPoint(_dispX + _dispW - insetX, _dispY + insetY);
        _quadBR = new WpfPoint(_dispX + _dispW - insetX, _dispY + _dispH - insetY);
        _quadBL = new WpfPoint(_dispX + insetX, _dispY + _dispH - insetY);
    }

    private void UpdateQuadOverlay()
    {
        if (CropCanvas == null || QuadPolygon == null || DarkOverlayPath == null || CropCanvas.ActualWidth <= 0 || CropCanvas.ActualHeight <= 0) return;

        Canvas.SetLeft(QuadThumbTL, _quadTL.X);
        Canvas.SetTop(QuadThumbTL, _quadTL.Y);

        Canvas.SetLeft(QuadThumbTR, _quadTR.X);
        Canvas.SetTop(QuadThumbTR, _quadTR.Y);

        Canvas.SetLeft(QuadThumbBR, _quadBR.X);
        Canvas.SetTop(QuadThumbBR, _quadBR.Y);

        Canvas.SetLeft(QuadThumbBL, _quadBL.X);
        Canvas.SetTop(QuadThumbBL, _quadBL.Y);

        QuadPolygon.Points = new PointCollection
        {
            _quadTL,
            _quadTR,
            _quadBR,
            _quadBL
        };

        var fullRect = new WpfRectangleGeometry(new WpfRect(0, 0, CropCanvas.ActualWidth, CropCanvas.ActualHeight));
        var quadGeo = new PathGeometry();
        var fig = new PathFigure { StartPoint = _quadTL, IsClosed = true };
        fig.Segments.Add(new LineSegment(_quadTR, true));
        fig.Segments.Add(new LineSegment(_quadBR, true));
        fig.Segments.Add(new LineSegment(_quadBL, true));
        quadGeo.Figures.Add(fig);
        DarkOverlayPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, quadGeo);

        double topDist = Math.Sqrt(Math.Pow(_quadTR.X - _quadTL.X, 2) + Math.Pow(_quadTR.Y - _quadTL.Y, 2));
        double botDist = Math.Sqrt(Math.Pow(_quadBR.X - _quadBL.X, 2) + Math.Pow(_quadBR.Y - _quadBL.Y, 2));
        double leftDist = Math.Sqrt(Math.Pow(_quadBL.X - _quadTL.X, 2) + Math.Pow(_quadBL.Y - _quadTL.Y, 2));
        double rightDist = Math.Sqrt(Math.Pow(_quadBR.X - _quadTR.X, 2) + Math.Pow(_quadBR.Y - _quadTR.Y, 2));
        double scale = _dispScale > 0 ? _dispScale : 1.0;
        double avgW = (topDist + botDist) / (2.0 * scale);
        double avgH = (leftDist + rightDist) / (2.0 * scale);
        TxtCropDimensions.Text = $"Quad Deskew: ~{avgW:F0} × ~{avgH:F0} px";
    }

    private void UpdateModeVisibility()
    {
        if (CropBox == null || QuadPolygon == null) return;

        var boxVis = _isQuadMode ? Visibility.Collapsed : Visibility.Visible;
        var quadVis = _isQuadMode ? Visibility.Visible : Visibility.Collapsed;

        CropBox.Visibility = boxVis;
        if (ThumbTL != null) ThumbTL.Visibility = boxVis;
        if (ThumbT != null) ThumbT.Visibility = boxVis;
        if (ThumbTR != null) ThumbTR.Visibility = boxVis;
        if (ThumbR != null) ThumbR.Visibility = boxVis;
        if (ThumbBR != null) ThumbBR.Visibility = boxVis;
        if (ThumbB != null) ThumbB.Visibility = boxVis;
        if (ThumbBL != null) ThumbBL.Visibility = boxVis;
        if (ThumbL != null) ThumbL.Visibility = boxVis;

        QuadPolygon.Visibility = quadVis;
        if (QuadThumbTL != null) QuadThumbTL.Visibility = quadVis;
        if (QuadThumbTR != null) QuadThumbTR.Visibility = quadVis;
        if (QuadThumbBR != null) QuadThumbBR.Visibility = quadVis;
        if (QuadThumbBL != null) QuadThumbBL.Visibility = quadVis;

        if (_isQuadMode)
        {
            UpdateQuadOverlay();
        }
        else
        {
            UpdateOverlayAndHandles();
        }
    }

    private void CropMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || CropBox == null || TxtStatus == null) return;

        _isQuadMode = RbModeQuad?.IsChecked == true;
        if (_isQuadMode && (_quadTL.X == 0 && _quadTL.Y == 0 && _quadTR.X == 0))
        {
            InitQuadPoints();
        }
        UpdateModeVisibility();
        if (TxtStatus != null)
        {
            TxtStatus.Text = _isQuadMode
                ? "📐 4-Corner Deskew Mode: Drag the 4 pins (TL, TR, BR, BL) to match the corners of your card/document."
                : "🔲 Box Crop Mode: Drag edges or corners to crop.";
        }
    }

    private void QuadThumbTL_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _quadTL = new WpfPoint(
            Math.Max(_dispX, Math.Min(_dispX + _dispW, _quadTL.X + e.HorizontalChange)),
            Math.Max(_dispY, Math.Min(_dispY + _dispH, _quadTL.Y + e.VerticalChange))
        );
        UpdateQuadOverlay();
    }

    private void QuadThumbTR_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _quadTR = new WpfPoint(
            Math.Max(_dispX, Math.Min(_dispX + _dispW, _quadTR.X + e.HorizontalChange)),
            Math.Max(_dispY, Math.Min(_dispY + _dispH, _quadTR.Y + e.VerticalChange))
        );
        UpdateQuadOverlay();
    }

    private void QuadThumbBR_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _quadBR = new WpfPoint(
            Math.Max(_dispX, Math.Min(_dispX + _dispW, _quadBR.X + e.HorizontalChange)),
            Math.Max(_dispY, Math.Min(_dispY + _dispH, _quadBR.Y + e.VerticalChange))
        );
        UpdateQuadOverlay();
    }

    private void QuadThumbBL_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _quadBL = new WpfPoint(
            Math.Max(_dispX, Math.Min(_dispX + _dispW, _quadBL.X + e.HorizontalChange)),
            Math.Max(_dispY, Math.Min(_dispY + _dispH, _quadBL.Y + e.VerticalChange))
        );
        UpdateQuadOverlay();
    }

    private async void AutoDetectCorners_Click(object sender, RoutedEventArgs e)
    {
        if (RbModeQuad != null) RbModeQuad.IsChecked = true;
        _isQuadMode = true;
        UpdateModeVisibility();
        TxtStatus.Text = "Detecting 4 document corners on-device...";
        try
        {
            var corners = await ImageCropService.Instance.DetectCardCornersAsync(_currentImagePath);
            if (corners != null && corners.Length == 4)
            {
                _quadTL = new WpfPoint(_dispX + corners[0].X * _dispScale, _dispY + corners[0].Y * _dispScale);
                _quadTR = new WpfPoint(_dispX + corners[1].X * _dispScale, _dispY + corners[1].Y * _dispScale);
                _quadBR = new WpfPoint(_dispX + corners[2].X * _dispScale, _dispY + corners[2].Y * _dispScale);
                _quadBL = new WpfPoint(_dispX + corners[3].X * _dispScale, _dispY + corners[3].Y * _dispScale);
                UpdateQuadOverlay();
                TxtStatus.Text = "✅ 4 corners detected! Drag pins to fine-tune if needed.";
            }
            else
            {
                TxtStatus.Text = "Could not detect distinct corners; corners initialized for manual adjustment.";
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Corner detection error: {ex.Message}";
        }
    }

    private void SnapCornersToCard_Click(object sender, RoutedEventArgs e)
    {
        if (_dispW <= 0 || _dispH <= 0) return;
        if (RbModeQuad != null) RbModeQuad.IsChecked = true;
        _isQuadMode = true;
        UpdateModeVisibility();

        double targetRatio = 8.5 / 5.5; // ID Card
        double w = _dispW * 0.85;
        double h = w / targetRatio;
        if (h > _dispH * 0.85)
        {
            h = _dispH * 0.85;
            w = h * targetRatio;
        }
        double cx = _dispX + _dispW / 2.0;
        double cy = _dispY + _dispH / 2.0;

        _quadTL = new WpfPoint(cx - w / 2.0, cy - h / 2.0);
        _quadTR = new WpfPoint(cx + w / 2.0, cy - h / 2.0);
        _quadBR = new WpfPoint(cx + w / 2.0, cy + h / 2.0);
        _quadBL = new WpfPoint(cx - w / 2.0, cy + h / 2.0);

        UpdateQuadOverlay();
        TxtStatus.Text = "📐 Snapped 4 corners to standard ID Card ratio (8.5 × 5.5 cm).";
    }
    #endregion

    private async void ApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        if (_dispScale <= 0) return;

        TxtStatus.Text = _isQuadMode ? "Straightening and cleaning document..." : "Cropping image...";
        try
        {
            if (_isQuadMode)
            {
                // Map 4 corner pins from canvas coordinates to source image pixel coordinates
                PointF tl = new PointF((float)((_quadTL.X - _dispX) / _dispScale), (float)((_quadTL.Y - _dispY) / _dispScale));
                PointF tr = new PointF((float)((_quadTR.X - _dispX) / _dispScale), (float)((_quadTR.Y - _dispY) / _dispScale));
                PointF br = new PointF((float)((_quadBR.X - _dispX) / _dispScale), (float)((_quadBR.Y - _dispY) / _dispScale));
                PointF bl = new PointF((float)((_quadBL.X - _dispX) / _dispScale), (float)((_quadBL.Y - _dispY) / _dispScale));

                int targetWidth = 0;
                int targetHeight = 0;
                if (RbIdCard?.IsChecked == true)
                {
                    // Standard 8.5 x 5.5 cm at 300 DPI: 1004 x 650 px
                    targetWidth = 1004;
                    targetHeight = 650;
                }
                else if (RbRation?.IsChecked == true)
                {
                    // 8.0 x 5.4 cm at 300 DPI: 945 x 638 px
                    targetWidth = 945;
                    targetHeight = 638;
                }

                bool removeShadows = ChkRemoveShadows?.IsChecked == true;
                string? result = await ImageCropService.Instance.WarpPerspectiveQuadAsync(
                    _currentImagePath,
                    new PointF[] { tl, tr, br, bl },
                    targetWidth,
                    targetHeight,
                    removeShadows);

                if (!string.IsNullOrEmpty(result) && File.Exists(result))
                {
                    CroppedImagePath = result;
                    DialogResult = true;
                    Close();
                }
                else
                {
                    TxtStatus.Text = "Failed to straighten perspective quad.";
                }
            }
            else
            {
                // Map canvas crop coordinates back to original image pixel coordinates
                int px = (int)Math.Round((_cropX - _dispX) / _dispScale);
                int py = (int)Math.Round((_cropY - _dispY) / _dispScale);
                int pw = (int)Math.Round(_cropW / _dispScale);
                int ph = (int)Math.Round(_cropH / _dispScale);

                var cropRect = new System.Drawing.Rectangle(px, py, pw, ph);
                string? result = await ImageCropService.Instance.CropImageToRectAsync(_currentImagePath, cropRect);

                if (!string.IsNullOrEmpty(result) && File.Exists(result))
                {
                    CroppedImagePath = result;
                    DialogResult = true;
                    Close();
                }
                else
                {
                    TxtStatus.Text = "Failed to save cropped image.";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply crop");
            TxtStatus.Text = $"Crop failed: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
