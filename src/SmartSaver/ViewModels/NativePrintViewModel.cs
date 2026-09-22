using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace SmartSaver.ViewModels;

public class NativePrintViewModel : ViewModelBase
{
    #region Win32 Native Driver Properties P/Invoke
    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int DocumentProperties(
        IntPtr hWnd,
        IntPtr hPrinter,
        [MarshalAs(UnmanagedType.LPTStr)] string pDeviceName,
        IntPtr pDevModeOutput,
        IntPtr pDevModeInput,
        int fMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private const int DM_UPDATE = 1;
    private const int DM_COPY = 2;
    private const int DM_PROMPT = 4;
    private const int DM_MODIFY = 8;

    private const int DM_OUT_BUFFER = DM_COPY;       // 2: copies printer defaults into buffer
    private const int DM_IN_BUFFER = DM_MODIFY;      // 8: modifies settings based on input buffer
    private const int DM_IN_PROMPT = DM_PROMPT;      // 4: displays driver UI setup property sheet

    private const int IDOK = 1;
    private const int IDCANCEL = 2;

    private const uint GMEM_MOVEABLE = 0x0002;
    #endregion

    private byte[]? _activeDevModeBytes = null;
    private readonly List<string> _imagePaths = new();
    private readonly List<byte[]> _imageBytes = new();
    private readonly string _documentTitle;

    public Func<IntPtr>? GetWindowHandle { get; set; }
    public Action<bool>? RequestClose { get; set; }

    #region Observable Collections
    public ObservableCollection<string> Printers { get; } = new();
    public ObservableCollection<string> PaperSizes { get; } = new();
    #endregion

    #region Printer Selection & Properties
    private string _selectedPrinter = string.Empty;
    public string SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetProperty(ref _selectedPrinter, value))
            {
                OnPrinterChanged();
            }
        }
    }

    private string _printerStatus = "Ready";
    public string PrinterStatus
    {
        get => _printerStatus;
        set => SetProperty(ref _printerStatus, value);
    }

    private int _copies = 1;
    public int Copies
    {
        get => _copies;
        set
        {
            int val = Math.Max(1, Math.Min(999, value));
            SetProperty(ref _copies, val);
        }
    }

    private bool _collate = true;
    public bool Collate
    {
        get => _collate;
        set => SetProperty(ref _collate, value);
    }

    private bool _isGrayscale = false;
    public bool IsGrayscale
    {
        get => _isGrayscale;
        set
        {
            if (SetProperty(ref _isGrayscale, value))
            {
                UpdatePreview();
            }
        }
    }

    private bool _pitchBlackText = false;
    public bool PitchBlackText
    {
        get => _pitchBlackText;
        set
        {
            if (SetProperty(ref _pitchBlackText, value))
            {
                UpdatePreview();
            }
        }
    }

    private bool _saveInkToner = false;
    public bool SaveInkToner
    {
        get => _saveInkToner;
        set => SetProperty(ref _saveInkToner, value);
    }
    #endregion

    #region Pages to Print
    private string _rangeMode = "All"; // "All", "Current", "Pages"
    public string RangeMode
    {
        get => _rangeMode;
        set
        {
            if (SetProperty(ref _rangeMode, value))
            {
                OnPropertyChanged(nameof(IsCustomPagesEnabled));
            }
        }
    }

    public bool IsCustomPagesEnabled => RangeMode == "Pages";

    private string _customPagesText = "1";
    public string CustomPagesText
    {
        get => _customPagesText;
        set => SetProperty(ref _customPagesText, value);
    }
    #endregion

    #region Page Sizing & Handling
    private string _sizingMode = "ActualSize"; // "ActualSize", "Fit", "Shrink", "CustomScale"
    public string SizingMode
    {
        get => _sizingMode;
        set
        {
            if (SetProperty(ref _sizingMode, value))
            {
                OnPropertyChanged(nameof(IsCustomScaleEnabled));
                UpdatePreview();
            }
        }
    }

    public bool IsCustomScaleEnabled => SizingMode == "CustomScale";

    private int _customScalePercent = 100;
    public int CustomScalePercent
    {
        get => _customScalePercent;
        set
        {
            int val = Math.Max(20, Math.Min(400, value));
            if (SetProperty(ref _customScalePercent, val))
            {
                UpdatePreview();
            }
        }
    }

    private bool _autoCenter = true;
    public bool AutoCenter
    {
        get => _autoCenter;
        set
        {
            if (SetProperty(ref _autoCenter, value))
            {
                UpdatePreview();
            }
        }
    }
    #endregion

    #region Orientation & Paper Size
    private string _orientationMode = "Portrait"; // "Auto", "Portrait", "Landscape"
    public string OrientationMode
    {
        get => _orientationMode;
        set
        {
            if (SetProperty(ref _orientationMode, value))
            {
                UpdatePreview();
            }
        }
    }

    private string _selectedPaperSize = "A4 (210 × 297 mm)";
    public string SelectedPaperSize
    {
        get => _selectedPaperSize;
        set
        {
            if (SetProperty(ref _selectedPaperSize, value))
            {
                UpdatePreview();
            }
        }
    }
    #endregion

    #region Live WYSIWYG Preview Properties
    private int _currentPageIndex = 0;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            int val = Math.Max(0, Math.Min(TotalPages - 1, value));
            if (SetProperty(ref _currentPageIndex, val))
            {
                UpdatePreview();
                OnPropertyChanged(nameof(PageSummaryText));
                OnPropertyChanged(nameof(CanGoPrevPage));
                OnPropertyChanged(nameof(CanGoNextPage));
                (PrevPageCommand as RelayCommand)?.OnCanExecuteChanged();
                (NextPageCommand as RelayCommand)?.OnCanExecuteChanged();
            }
        }
    }

    public int TotalPages => Math.Max(1, _imageBytes.Count > 0 ? _imageBytes.Count : _imagePaths.Count);
    public int MaxPageIndex => Math.Max(0, TotalPages - 1);
    public bool HasMultiplePages => TotalPages > 1;

    public string PageSummaryText => $"Page {CurrentPageIndex + 1} of {TotalPages}";

    public bool CanGoPrevPage => CurrentPageIndex > 0;
    public bool CanGoNextPage => CurrentPageIndex < TotalPages - 1;

    private ImageSource? _previewImage;
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set => SetProperty(ref _previewImage, value);
    }

    private string _calculatedScaleText = "Scale: 100%";
    public string CalculatedScaleText
    {
        get => _calculatedScaleText;
        set => SetProperty(ref _calculatedScaleText, value);
    }

    private string _paperDimensionsText = "A4 • 8.27 × 11.69 Inches (210 × 297 mm)";
    public string PaperDimensionsText
    {
        get => _paperDimensionsText;
        set => SetProperty(ref _paperDimensionsText, value);
    }

    private double _paperAspectWidth = 210;
    public double PaperAspectWidth
    {
        get => _paperAspectWidth;
        set => SetProperty(ref _paperAspectWidth, value);
    }

    private double _paperAspectHeight = 297;
    public double PaperAspectHeight
    {
        get => _paperAspectHeight;
        set => SetProperty(ref _paperAspectHeight, value);
    }

    private double _previewImageScale = 1.0;
    public double PreviewImageScale
    {
        get => _previewImageScale;
        set => SetProperty(ref _previewImageScale, value);
    }

    private bool _isPrinting = false;
    public bool IsPrinting
    {
        get => _isPrinting;
        set => SetProperty(ref _isPrinting, value);
    }

    private string _printStatusText = string.Empty;
    public string PrintStatusText
    {
        get => _printStatusText;
        set => SetProperty(ref _printStatusText, value);
    }
    #endregion

    #region Commands
    public ICommand OpenPropertiesCommand { get; }
    public ICommand PageSetupCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand SetSizingModeCommand { get; }
    public ICommand SetOrientationCommand { get; }
    public ICommand IncreaseCopiesCommand { get; }
    public ICommand DecreaseCopiesCommand { get; }
    #endregion

    public NativePrintViewModel(IReadOnlyList<string> imagePaths, string documentTitle = "DASMO Print Job")
    {
        _documentTitle = string.IsNullOrWhiteSpace(documentTitle) ? "DASMO Print Job" : documentTitle;

        if (imagePaths != null)
        {
            foreach (var path in imagePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        _imageBytes.Add(File.ReadAllBytes(path));
                        _imagePaths.Add(path);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to load print image {Path}", path);
                    }
                }
            }
        }

        // Initialize Commands
        OpenPropertiesCommand = new RelayCommand(_ => OpenDriverProperties());
        PageSetupCommand = new RelayCommand(_ => OpenPageSetup());
        PrintCommand = new RelayCommand(async _ => await ExecutePrintAsync(), _ => !IsPrinting && !string.IsNullOrEmpty(SelectedPrinter));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false));

        PrevPageCommand = new RelayCommand(_ => { if (CanGoPrevPage) CurrentPageIndex--; }, _ => CanGoPrevPage);
        NextPageCommand = new RelayCommand(_ => { if (CanGoNextPage) CurrentPageIndex++; }, _ => CanGoNextPage);

        SetSizingModeCommand = new RelayCommand(param =>
        {
            if (param is string mode) SizingMode = mode;
        });

        SetOrientationCommand = new RelayCommand(param =>
        {
            if (param is string ori) OrientationMode = ori;
        });

        IncreaseCopiesCommand = new RelayCommand(_ => Copies++);
        DecreaseCopiesCommand = new RelayCommand(_ => { if (Copies > 1) Copies--; });

        // Initialize Printers & Paper Sizes
        InitializePrinters();
        InitializePaperSizes();

        // Initial preview render
        UpdatePreview();
    }

    public NativePrintViewModel(ImageSource singleImage, string documentTitle = "DASMO Print Job", string? preferredPaperSize = null, string? preferredOrientation = null)
        : this(ConvertImageSourceToTempFile(singleImage), documentTitle)
    {
        if (!string.IsNullOrEmpty(preferredPaperSize))
        {
            foreach (var item in PaperSizes)
            {
                if (item.StartsWith(preferredPaperSize, StringComparison.OrdinalIgnoreCase) ||
                    (preferredPaperSize.StartsWith("4", StringComparison.OrdinalIgnoreCase) && item.StartsWith("4", StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedPaperSize = item;
                    break;
                }
            }
        }

        if (!string.IsNullOrEmpty(preferredOrientation))
        {
            OrientationMode = preferredOrientation;
        }
        else if (singleImage is BitmapSource bmp && bmp.PixelWidth > bmp.PixelHeight)
        {
            OrientationMode = "Landscape";
        }
    }

    private static List<string> ConvertImageSourceToTempFile(ImageSource imageSource)
    {
        var list = new List<string>();
        if (imageSource is BitmapSource bmp)
        {
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"smartsaver_print_{Guid.NewGuid():N}.png");
                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                encoder.Save(fs);
                list.Add(tempPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to convert ImageSource to temp image for printing");
            }
        }
        return list;
    }

    private void InitializePrinters()
    {
        try
        {
            Printers.Clear();
            string defaultPrinterName = string.Empty;

            try
            {
                var defaultSettings = new PrinterSettings();
                defaultPrinterName = defaultSettings.PrinterName;
            }
            catch { }

            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                Printers.Add(printer);
            }

            if (Printers.Count == 0)
            {
                Printers.Add("Microsoft Print to PDF");
                Printers.Add("Default Printer");
            }

            if (!string.IsNullOrEmpty(defaultPrinterName) && Printers.Contains(defaultPrinterName))
            {
                SelectedPrinter = defaultPrinterName;
            }
            else
            {
                SelectedPrinter = Printers.FirstOrDefault() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enumerate installed printers");
        }
    }

    private void InitializePaperSizes()
    {
        PaperSizes.Clear();
        PaperSizes.Add("A4 (210 × 297 mm)");
        PaperSizes.Add("Letter (8.5 × 11 in)");
        PaperSizes.Add("Legal (8.5 × 14 in)");
        PaperSizes.Add("4 × 6 in Photo (10 × 15 cm)");
        PaperSizes.Add("A3 (297 × 420 mm)");
        PaperSizes.Add("A5 (148 × 210 mm)");

        SelectedPaperSize = "A4 (210 × 297 mm)";
    }

    private void OnPrinterChanged()
    {
        _activeDevModeBytes = null; // Reset driver DEVMODE when switching printers
        try
        {
            if (string.IsNullOrEmpty(SelectedPrinter))
            {
                PrinterStatus = "No Printer Selected";
                return;
            }

            var ps = new PrinterSettings { PrinterName = SelectedPrinter };
            PrinterStatus = ps.IsValid ? "Ready" : "Offline / Unavailable";
        }
        catch
        {
            PrinterStatus = "Ready";
        }
    }

    private void OpenDriverProperties()
    {
        if (string.IsNullOrWhiteSpace(SelectedPrinter)) return;

        try
        {
            IntPtr hWnd = GetWindowHandle?.Invoke() ?? IntPtr.Zero;

            if (!OpenPrinter(SelectedPrinter, out IntPtr hPrinter, IntPtr.Zero))
            {
                WpfMessageBox.Show($"Could not open printer '{SelectedPrinter}'.", "Printer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 1. Query required buffer size for DEVMODE (includes driver vendor private extra data)
                int size = DocumentProperties(hWnd, hPrinter, SelectedPrinter, IntPtr.Zero, IntPtr.Zero, 0);
                if (size <= 0)
                {
                    WpfMessageBox.Show($"Could not query driver properties for '{SelectedPrinter}'.", "Printer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                IntPtr hDevMode = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)size);
                if (hDevMode == IntPtr.Zero) return;

                try
                {
                    IntPtr pDevMode = GlobalLock(hDevMode);
                    if (pDevMode == IntPtr.Zero) return;

                    try
                    {
                        // 2. Initialize buffer: if we have previous custom devmode bytes of matching size, reuse them;
                        // otherwise query current printer defaults (DM_OUT_BUFFER = 2).
                        if (_activeDevModeBytes != null && _activeDevModeBytes.Length == size)
                        {
                            Marshal.Copy(_activeDevModeBytes, 0, pDevMode, size);
                        }
                        else
                        {
                            DocumentProperties(hWnd, hPrinter, SelectedPrinter, pDevMode, IntPtr.Zero, DM_OUT_BUFFER);
                        }

                        // 3. Mode 14 = DM_IN_PROMPT (4) | DM_IN_BUFFER (8) | DM_OUT_BUFFER (2)
                        // Displays the manufacturer's rich printing preferences dialog (e.g. Brother Basic, Advanced, Print Profiles, etc.)
                        int result = DocumentProperties(hWnd, hPrinter, SelectedPrinter, pDevMode, pDevMode, DM_IN_PROMPT | DM_IN_BUFFER | DM_OUT_BUFFER);

                        if (result == IDOK) // 1 = User clicked OK
                        {
                            // Save updated devmode bytes (contains paper type, print quality, color, duplex, etc.)
                            _activeDevModeBytes = new byte[size];
                            Marshal.Copy(pDevMode, _activeDevModeBytes, 0, size);

                            GlobalUnlock(hDevMode);
                            pDevMode = IntPtr.Zero; // Prevent double unlock in finally

                            // Synchronize settings with PrinterSettings to update UI controls
                            var ps = new PrinterSettings { PrinterName = SelectedPrinter };
                            ps.SetHdevmode(hDevMode);
                            ps.DefaultPageSettings.SetHdevmode(hDevMode);

                            if (ps.Copies > 0) Copies = ps.Copies;
                            OrientationMode = ps.DefaultPageSettings.Landscape ? "Landscape" : "Portrait";
                            IsGrayscale = !ps.DefaultPageSettings.Color;

                            string driverPaperName = ps.DefaultPageSettings.PaperSize?.PaperName ?? string.Empty;
                            SyncPaperSizeFromDriver(driverPaperName);

                            UpdatePreview();
                            Log.Information("Printer preferences updated for {Printer}. Orientation={Ori}, Color={Col}, Copies={Copies}, Paper={Paper}",
                                SelectedPrinter, OrientationMode, !IsGrayscale, Copies, driverPaperName);
                        }
                    }
                    finally
                    {
                        if (pDevMode != IntPtr.Zero)
                        {
                            GlobalUnlock(hDevMode);
                        }
                    }
                }
                finally
                {
                    GlobalFree(hDevMode);
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open printer driver properties for {Printer}", SelectedPrinter);
            WpfMessageBox.Show($"Could not open printer properties:\n{ex.Message}", "Printer Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SyncPaperSizeFromDriver(string driverPaperName)
    {
        if (string.IsNullOrWhiteSpace(driverPaperName)) return;

        foreach (var item in PaperSizes)
        {
            if (item.StartsWith(driverPaperName, StringComparison.OrdinalIgnoreCase) ||
                (driverPaperName.StartsWith("Letter", StringComparison.OrdinalIgnoreCase) && item.StartsWith("Letter", StringComparison.OrdinalIgnoreCase)) ||
                (driverPaperName.StartsWith("A4", StringComparison.OrdinalIgnoreCase) && item.StartsWith("A4", StringComparison.OrdinalIgnoreCase)) ||
                (driverPaperName.StartsWith("Legal", StringComparison.OrdinalIgnoreCase) && item.StartsWith("Legal", StringComparison.OrdinalIgnoreCase)) ||
                (driverPaperName.StartsWith("A3", StringComparison.OrdinalIgnoreCase) && item.StartsWith("A3", StringComparison.OrdinalIgnoreCase)) ||
                (driverPaperName.StartsWith("A5", StringComparison.OrdinalIgnoreCase) && item.StartsWith("A5", StringComparison.OrdinalIgnoreCase)))
            {
                SelectedPaperSize = item;
                return;
            }
        }

        if (!PaperSizes.Contains(driverPaperName))
        {
            PaperSizes.Add(driverPaperName);
        }
        SelectedPaperSize = driverPaperName;
    }

    private void OpenPageSetup()
    {
        try
        {
            using var psd = new System.Windows.Forms.PageSetupDialog();
            var ps = new PrinterSettings { PrinterName = SelectedPrinter };

            if (_activeDevModeBytes != null && _activeDevModeBytes.Length > 0)
            {
                IntPtr hDevMode = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)_activeDevModeBytes.Length);
                if (hDevMode != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr pDevMode = GlobalLock(hDevMode);
                        if (pDevMode != IntPtr.Zero)
                        {
                            Marshal.Copy(_activeDevModeBytes, 0, pDevMode, _activeDevModeBytes.Length);
                            GlobalUnlock(hDevMode);

                            ps.SetHdevmode(hDevMode);
                            ps.DefaultPageSettings.SetHdevmode(hDevMode);
                        }
                    }
                    finally
                    {
                        GlobalFree(hDevMode);
                    }
                }
            }

            psd.PageSettings = ps.DefaultPageSettings;
            psd.PrinterSettings = ps;
            psd.EnableMetric = true;

            if (psd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                if (psd.PageSettings.Landscape)
                    OrientationMode = "Landscape";
                else
                    OrientationMode = "Portrait";

                UpdatePreview();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open Page Setup dialog");
        }
    }

    public void UpdatePreview()
    {
        if (_imageBytes.Count == 0 || CurrentPageIndex >= _imageBytes.Count)
        {
            PreviewImage = null;
            return;
        }

        try
        {
            byte[] rawBytes = _imageBytes[CurrentPageIndex];
            using var ms = new MemoryStream(rawBytes);

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();

            // Determine effective orientation
            bool isLandscape = false;
            if (OrientationMode == "Landscape")
            {
                isLandscape = true;
            }
            else if (OrientationMode == "Auto")
            {
                isLandscape = bi.PixelWidth > bi.PixelHeight;
            }

            // Paper aspect ratio setup
            double baseWidthMm = 210;
            double baseHeightMm = 297;
            string paperName = "A4";

            if (SelectedPaperSize.StartsWith("Letter", StringComparison.OrdinalIgnoreCase))
            {
                baseWidthMm = 215.9; baseHeightMm = 279.4; paperName = "Letter";
            }
            else if (SelectedPaperSize.StartsWith("Legal", StringComparison.OrdinalIgnoreCase))
            {
                baseWidthMm = 215.9; baseHeightMm = 355.6; paperName = "Legal";
            }
            else if (SelectedPaperSize.StartsWith("4", StringComparison.OrdinalIgnoreCase))
            {
                baseWidthMm = 101.6; baseHeightMm = 152.4; paperName = "4×6 Photo";
            }
            else if (SelectedPaperSize.StartsWith("A3", StringComparison.OrdinalIgnoreCase))
            {
                baseWidthMm = 297.0; baseHeightMm = 420.0; paperName = "A3";
            }
            else if (SelectedPaperSize.StartsWith("A5", StringComparison.OrdinalIgnoreCase))
            {
                baseWidthMm = 148.0; baseHeightMm = 210.0; paperName = "A5";
            }

            if (isLandscape)
            {
                PaperAspectWidth = baseHeightMm;
                PaperAspectHeight = baseWidthMm;
                PaperDimensionsText = $"{paperName} Landscape • {baseHeightMm:F0} × {baseWidthMm:F0} mm ({(baseHeightMm / 25.4):F2} × {(baseWidthMm / 25.4):F2} in)";
            }
            else
            {
                PaperAspectWidth = baseWidthMm;
                PaperAspectHeight = baseHeightMm;
                PaperDimensionsText = $"{paperName} Portrait • {baseWidthMm:F0} × {baseHeightMm:F0} mm ({(baseWidthMm / 25.4):F2} × {(baseHeightMm / 25.4):F2} in)";
            }

            // Calculate Effective Scale
            double scale = 1.0;
            if (SizingMode == "CustomScale")
            {
                scale = CustomScalePercent / 100.0;
                CalculatedScaleText = $"Scale: {CustomScalePercent}%";
            }
            else if (SizingMode == "ActualSize")
            {
                scale = 1.0;
                CalculatedScaleText = "Scale: 100% (Actual 1:1)";
            }
            else // Fit
            {
                double printableRatioX = (PaperAspectWidth - 10) / PaperAspectWidth;
                double printableRatioY = (PaperAspectHeight - 10) / PaperAspectHeight;
                scale = Math.Min(printableRatioX, printableRatioY);
                CalculatedScaleText = $"Scale: {scale * 100.0:F0}% (Fit)";
            }

            PreviewImageScale = scale;

            // Pitch black text or Grayscale transformation for preview
            if (PitchBlackText)
            {
                PreviewImage = ApplyPitchBlackToBitmapSource(bi);
            }
            else if (IsGrayscale)
            {
                var grayBmp = new FormatConvertedBitmap(bi, PixelFormats.Gray8, null, 0);
                grayBmp.Freeze();
                PreviewImage = grayBmp;
            }
            else
            {
                PreviewImage = bi;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to render print preview");
        }
    }

    private List<int> GetPagesToPrintIndices()
    {
        var list = new List<int>();
        int total = TotalPages;

        if (RangeMode == "Current")
        {
            list.Add(CurrentPageIndex);
            return list;
        }

        if (RangeMode == "Pages" && !string.IsNullOrWhiteSpace(CustomPagesText))
        {
            // Parse ranges like "1, 3-5"
            var parts = CustomPagesText.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Contains('-'))
                {
                    var range = trimmed.Split('-');
                    if (range.Length == 2 && int.TryParse(range[0], out int start) && int.TryParse(range[1], out int end))
                    {
                        for (int p = Math.Min(start, end); p <= Math.Max(start, end); p++)
                        {
                            if (p >= 1 && p <= total) list.Add(p - 1);
                        }
                    }
                }
                else if (int.TryParse(trimmed, out int single))
                {
                    if (single >= 1 && single <= total) list.Add(single - 1);
                }
            }

            if (list.Count > 0) return list.Distinct().OrderBy(x => x).ToList();
        }

        // Default: All pages
        for (int i = 0; i < total; i++) list.Add(i);
        return list;
    }

    private async Task ExecutePrintAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPrinter))
        {
            WpfMessageBox.Show("Please select a printer.", "No Printer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pagesToPrint = GetPagesToPrintIndices();
        if (pagesToPrint.Count == 0)
        {
            WpfMessageBox.Show("No valid pages selected to print.", "Invalid Page Range", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsPrinting = true;
        PrintStatusText = $"Sending {pagesToPrint.Count} page(s) ({Copies} copies) to {SelectedPrinter}...";

        try
        {
            bool success = await Task.Run(() =>
            {
                try
                {
                    using var pd = new PrintDocument();
                    pd.DocumentName = _documentTitle;
                    pd.PrinterSettings.PrinterName = SelectedPrinter;

                    // Apply active driver DEVMODE (Media type, photo quality, ink density, duplex, etc.) if configured
                    if (_activeDevModeBytes != null && _activeDevModeBytes.Length > 0)
                    {
                        IntPtr hDevMode = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)_activeDevModeBytes.Length);
                        if (hDevMode != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr pDevMode = GlobalLock(hDevMode);
                                if (pDevMode != IntPtr.Zero)
                                {
                                    Marshal.Copy(_activeDevModeBytes, 0, pDevMode, _activeDevModeBytes.Length);
                                    GlobalUnlock(hDevMode);

                                    pd.PrinterSettings.SetHdevmode(hDevMode);
                                    pd.DefaultPageSettings.SetHdevmode(hDevMode);
                                }
                            }
                            finally
                            {
                                GlobalFree(hDevMode);
                            }
                        }
                    }

                    pd.PrinterSettings.Copies = (short)Math.Max(1, Copies);
                    pd.PrinterSettings.Collate = Collate;

                    // Match paper size
                    foreach (PaperSize ps in pd.PrinterSettings.PaperSizes)
                    {
                        if (SelectedPaperSize.StartsWith("A4", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.A4)
                        {
                            pd.DefaultPageSettings.PaperSize = ps;
                            break;
                        }
                        else if (SelectedPaperSize.StartsWith("Letter", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.Letter)
                        {
                            pd.DefaultPageSettings.PaperSize = ps;
                            break;
                        }
                        else if (SelectedPaperSize.StartsWith("Legal", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.Legal)
                        {
                            pd.DefaultPageSettings.PaperSize = ps;
                            break;
                        }
                        else if (SelectedPaperSize.StartsWith("A3", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.A3)
                        {
                            pd.DefaultPageSettings.PaperSize = ps;
                            break;
                        }
                        else if (SelectedPaperSize.StartsWith("A5", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.A5)
                        {
                            pd.DefaultPageSettings.PaperSize = ps;
                            break;
                        }
                        else if (SelectedPaperSize.StartsWith("4", StringComparison.OrdinalIgnoreCase) &&
                                 (ps.Kind == PaperKind.JapanesePostcard ||
                                  (Math.Abs(ps.Width - 400) <= 25 && Math.Abs(ps.Height - 600) <= 25) ||
                                  (Math.Abs(ps.Width - 600) <= 25 && Math.Abs(ps.Height - 400) <= 25) ||
                                  ps.PaperName.IndexOf("4x6", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  ps.PaperName.IndexOf("4 x 6", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  ps.PaperName.IndexOf("10x15", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  ps.PaperName.IndexOf("10 x 15", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  ps.PaperName.IndexOf("KG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  ps.PaperName.IndexOf("Postcard", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            pd.DefaultPageSettings.PaperSize = ps;
                            break;
                        }
                    }

                    // Determine orientation
                    bool isLandscape = false;
                    if (OrientationMode == "Landscape") isLandscape = true;
                    else if (OrientationMode == "Portrait") isLandscape = false;

                    pd.DefaultPageSettings.Landscape = isLandscape;
                    pd.DefaultPageSettings.Color = !IsGrayscale;

                    int pageCounter = 0;

                    pd.PrintPage += (sender, e) =>
                    {
                        if (e.Graphics == null) return;

                        if (pageCounter >= pagesToPrint.Count)
                        {
                            e.HasMorePages = false;
                            return;
                        }

                        int pageIdx = pagesToPrint[pageCounter];
                        byte[] rawBytes = _imageBytes[pageIdx];

                        using var ms = new MemoryStream(rawBytes);
                        using var baseImg = System.Drawing.Image.FromStream(ms);
                        using var gdiImage = PitchBlackText ? ApplyPitchBlackToGdi(baseImg) : (System.Drawing.Image)baseImg.Clone();

                        // If Auto orientation, update per-page landscape
                        if (OrientationMode == "Auto")
                        {
                            e.PageSettings.Landscape = gdiImage.Width > gdiImage.Height;
                        }

                        System.Drawing.Rectangle pageBounds = e.PageBounds;
                        System.Drawing.Rectangle marginBounds = e.MarginBounds;
                        if (marginBounds.Width <= 0 || marginBounds.Height <= 0) marginBounds = pageBounds;

                        // Resolve true DPI:
                        float dpiX = gdiImage.HorizontalResolution;
                        float dpiY = gdiImage.VerticalResolution;

                        // Smart DPI detection:
                        // If DPI is <= 0 or defaulted to standard screen ~96 DPI but image is high-res (e.g. >= 2000px wide for A4),
                        // clamp to 300 DPI (standard print canvas resolution).
                        if (dpiX <= 0f || (Math.Abs(dpiX - 96.0f) < 2.0f && gdiImage.Width >= 2000))
                        {
                            dpiX = 300f;
                        }
                        if (dpiY <= 0f || (Math.Abs(dpiY - 96.0f) < 2.0f && gdiImage.Height >= 2500))
                        {
                            dpiY = 300f;
                        }

                        float widthHundredths = (gdiImage.Width / dpiX) * 100f;
                        float heightHundredths = (gdiImage.Height / dpiY) * 100f;

                        System.Drawing.Rectangle destRect;

                        if (SizingMode == "ActualSize")
                        {
                            // In GDI+, print coordinates are 1/100 inch.
                            // AutoCenter: Centers the physical 1:1 image exactly on the paper (e.g. A4 on A4 has x≈0, y≈0).
                            float x = AutoCenter ? (pageBounds.Width - widthHundredths) / 2f : 0f;
                            float y = AutoCenter ? (pageBounds.Height - heightHundredths) / 2f : 0f;

                            destRect = new System.Drawing.Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(widthHundredths), (int)Math.Round(heightHundredths));
                        }
                        else if (SizingMode == "CustomScale")
                        {
                            float scale = CustomScalePercent / 100f;
                            float width = widthHundredths * scale;
                            float height = heightHundredths * scale;
                            float x = AutoCenter ? (pageBounds.Width - width) / 2f : marginBounds.Left;
                            float y = AutoCenter ? (pageBounds.Height - height) / 2f : marginBounds.Top;
                            destRect = new System.Drawing.Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(width), (int)Math.Round(height));
                        }
                        else // Fit or Shrink
                        {
                            float scaleX = (float)marginBounds.Width / widthHundredths;
                            float scaleY = (float)marginBounds.Height / heightHundredths;
                            float scale = Math.Min(scaleX, scaleY);

                            if (SizingMode == "Shrink" && scale > 1.0f) scale = 1.0f;

                            int destWidth = (int)Math.Round(widthHundredths * scale);
                            int destHeight = (int)Math.Round(heightHundredths * scale);
                            int x = AutoCenter ? marginBounds.Left + (marginBounds.Width - destWidth) / 2 : marginBounds.Left;
                            int y = AutoCenter ? marginBounds.Top + (marginBounds.Height - destHeight) / 2 : marginBounds.Top;
                            destRect = new System.Drawing.Rectangle(x, y, destWidth, destHeight);
                        }

                        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        e.Graphics.SmoothingMode = SmoothingMode.HighQuality;

                        if (IsGrayscale)
                        {
                            var grayMatrix = new ColorMatrix(new float[][]
                            {
                                new float[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                                new float[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                                new float[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                                new float[] { 0,      0,      0,      1, 0 },
                                new float[] { 0,      0,      0,      0, 1 }
                            });
                            using var ia = new ImageAttributes();
                            ia.SetColorMatrix(grayMatrix);
                            e.Graphics.DrawImage(gdiImage, destRect, 0, 0, gdiImage.Width, gdiImage.Height, GraphicsUnit.Pixel, ia);
                        }
                        else
                        {
                            e.Graphics.DrawImage(gdiImage, destRect);
                        }

                        pageCounter++;
                        e.HasMorePages = pageCounter < pagesToPrint.Count;
                    };

                    pd.Print();
                    Log.Information("Native print job sent successfully: {Title} ({Pages} pages)", _documentTitle, pagesToPrint.Count);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "PrintDocument execution failed");
                    return false;
                }
            });

            if (success)
            {
                PrintStatusText = "✅ Print sent successfully!";
                RequestClose?.Invoke(true);
            }
            else
            {
                PrintStatusText = "❌ Failed to print document.";
                WpfMessageBox.Show("Printing failed. Please check your printer connection and driver.", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error executing print");
            WpfMessageBox.Show($"Printing error:\n{ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsPrinting = false;
        }
    }

    private static System.Drawing.Bitmap ApplyPitchBlackToGdi(System.Drawing.Image src)
    {
        var bmp = new System.Drawing.Bitmap(src);
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            int bytes = Math.Abs(bmpData.Stride) * bmp.Height;
            byte[] rgbValues = new byte[bytes];
            Marshal.Copy(bmpData.Scan0, rgbValues, 0, bytes);

            for (int i = 0; i < bytes; i += 4)
            {
                byte b = rgbValues[i];
                byte g = rgbValues[i + 1];
                byte r = rgbValues[i + 2];

                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                int maxDiff = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(r - b), Math.Abs(g - b)));

                if (maxDiff <= 35)
                {
                    if (lum >= 175)
                    {
                        rgbValues[i] = 255;
                        rgbValues[i + 1] = 255;
                        rgbValues[i + 2] = 255;
                    }
                    else if (lum < 135)
                    {
                        rgbValues[i] = 0;
                        rgbValues[i + 1] = 0;
                        rgbValues[i + 2] = 0;
                    }
                    else
                    {
                        float factor = (lum - 135.0f) / 40.0f;
                        byte val = (byte)Math.Clamp(factor * 255.0f, 0, 255);
                        rgbValues[i] = val;
                        rgbValues[i + 1] = val;
                        rgbValues[i + 2] = val;
                    }
                }
            }

            Marshal.Copy(rgbValues, 0, bmpData.Scan0, bytes);
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }

        return bmp;
    }

    private static BitmapSource ApplyPitchBlackToBitmapSource(BitmapSource source)
    {
        var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = formatted.PixelWidth;
        int height = formatted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        formatted.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];

            float lum = 0.299f * r + 0.587f * g + 0.114f * b;
            int maxDiff = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(r - b), Math.Abs(g - b)));

            if (maxDiff <= 35)
            {
                if (lum >= 175)
                {
                    pixels[i] = 255;
                    pixels[i + 1] = 255;
                    pixels[i + 2] = 255;
                }
                else if (lum < 135)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                }
                else
                {
                    float factor = (lum - 135.0f) / 40.0f;
                    byte val = (byte)Math.Clamp(factor * 255.0f, 0, 255);
                    pixels[i] = val;
                    pixels[i + 1] = val;
                    pixels[i + 2] = val;
                }
            }
        }

        var result = BitmapSource.Create(width, height, formatted.DpiX, formatted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
