using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using WpfClipboard = System.Windows.Clipboard;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace SmartSaver.ViewModels;

public class GovtCardExtractorViewModel : ViewModelBase
{
    private readonly GovtCardExtractorService _extractorService;
    private readonly CardSheetGeneratorService _sheetService;

    public ObservableCollection<ExtractedCardItem> Cards { get; } = new();

    private GovtDocumentType _selectedDocType = GovtDocumentType.AutoDetect;
    public GovtDocumentType SelectedDocType
    {
        get => _selectedDocType;
        set
        {
            if (SetProperty(ref _selectedDocType, value))
            {
                OnPropertyChanged(nameof(DocTypeDescription));
                _ = RefreshExtractionAsync();
            }
        }
    }

    public string DocTypeDescription => SelectedDocType switch
    {
        GovtDocumentType.AutoDetect => "🤖 Smart Auto-Detect per Document (Mix Ration, Aadhaar, PAN, Voter in 1 Batch)",
        GovtDocumentType.RationCardWB => "🍚 West Bengal / NFSA e-Ration Card (Auto-crops bottom card below scissor line)",
        GovtDocumentType.AadhaarCard => "🆔 UIDAI e-Aadhaar PDF (Auto-extracts bottom Aadhaar Front & Back)",
        GovtDocumentType.PanCard => "💳 NSDL / UTIITSL e-PAN Card (Auto-extracts physical PAN card)",
        GovtDocumentType.VoterCard => "🗳️ ECI e-EPIC Voter Card (Auto-extracts Voter ID)",
        GovtDocumentType.AyushmanCard => "🏥 PM-JAY Ayushman Bharat Health Card",
        GovtDocumentType.FullPageCard => "📄 Full Page or Pre-Cropped ID Card Image",
        _ => "📐 Custom Crop Box"
    };

    private CardSheetLayout _selectedLayout = CardSheetLayout.FiveCardsA4PaperSaver;
    public CardSheetLayout SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (SetProperty(ref _selectedLayout, value))
            {
                OnPropertyChanged(nameof(LayoutDescription));
                UpdatePagination();
                _ = UpdatePreviewAsync();
            }
        }
    }

    public string LayoutDescription => SelectedLayout switch
    {
        CardSheetLayout.FiveCardsA4PaperSaver => "⚡ 5 Cards / 10 Sides per A4 Sheet (5 Fronts Left, 5 Backs Right — Max Paper Saver)",
        CardSheetLayout.TwoCardsA4 => "📄 2 Cards / 4 Sides per A4 Sheet",
        CardSheetLayout.SingleCardSideBySide => "📑 1 Card Side-by-Side (Center Fold Line for Instant Lamination)",
        CardSheetLayout.SingleCardTopBottom => "⬇️ 1 Card Top / Bottom (Standard ID Card Stack)",
        _ => "Custom Layout"
    };

    private CardSheetAlignment _selectedAlignment = CardSheetAlignment.TopToBottomPaperSaver;
    public CardSheetAlignment SelectedAlignment
    {
        get => _selectedAlignment;
        set
        {
            if (SetProperty(ref _selectedAlignment, value))
            {
                OnPropertyChanged(nameof(AlignmentDescription));
                _ = UpdatePreviewAsync();
            }
        }
    }

    public string AlignmentDescription => SelectedAlignment switch
    {
        CardSheetAlignment.TopToBottomPaperSaver => "⬆️ Top-to-Bottom (Paper Re-Use: Cards pack at top so you can scissor-cut & reuse unused bottom blank paper)",
        _ => "↕️ Centered on Page"
    };

    private double _cardWidthCm = 8.00;
    public double CardWidthCm
    {
        get => _cardWidthCm;
        set
        {
            if (SetProperty(ref _cardWidthCm, Math.Round(value, 2)))
            {
                OnPropertyChanged(nameof(CardDimensionsText));
                OnPropertyChanged(nameof(SheetSummaryText));
                UpdatePresetIndexFromDimensions();
                _ = UpdatePreviewAsync();
            }
        }
    }

    private double _cardHeightCm = 5.40;
    public double CardHeightCm
    {
        get => _cardHeightCm;
        set
        {
            if (SetProperty(ref _cardHeightCm, Math.Round(value, 2)))
            {
                OnPropertyChanged(nameof(CardDimensionsText));
                OnPropertyChanged(nameof(SheetSummaryText));
                UpdatePresetIndexFromDimensions();
                _ = UpdatePreviewAsync();
            }
        }
    }

    public string CardDimensionsText => $"{CardWidthCm:F2} cm × {CardHeightCm:F2} cm";

    private int _cardSizePresetIndex = 0;
    public int CardSizePresetIndex
    {
        get => _cardSizePresetIndex;
        set
        {
            if (SetProperty(ref _cardSizePresetIndex, value))
            {
                switch (value)
                {
                    case 0: // Standard Wallet / Pouch (Recommended for Ayushman & Ration)
                        _cardWidthCm = 8.00;
                        _cardHeightCm = 5.40;
                        break;
                    case 1: // Standard CR-80 / ID-1 strict ISO
                        _cardWidthCm = 8.56;
                        _cardHeightCm = 5.40;
                        break;
                    case 2: // Compact
                        _cardWidthCm = 7.80;
                        _cardHeightCm = 5.20;
                        break;
                }
                OnPropertyChanged(nameof(CardWidthCm));
                OnPropertyChanged(nameof(CardHeightCm));
                OnPropertyChanged(nameof(CardDimensionsText));
                OnPropertyChanged(nameof(SheetSummaryText));
                _ = UpdatePreviewAsync();
            }
        }
    }

    private void UpdatePresetIndexFromDimensions()
    {
        if (Math.Abs(_cardWidthCm - 8.00) < 0.01 && Math.Abs(_cardHeightCm - 5.40) < 0.01)
        {
            if (_cardSizePresetIndex != 0) { _cardSizePresetIndex = 0; OnPropertyChanged(nameof(CardSizePresetIndex)); }
        }
        else if (Math.Abs(_cardWidthCm - 8.56) < 0.01 && Math.Abs(_cardHeightCm - 5.40) < 0.01)
        {
            if (_cardSizePresetIndex != 1) { _cardSizePresetIndex = 1; OnPropertyChanged(nameof(CardSizePresetIndex)); }
        }
        else if (Math.Abs(_cardWidthCm - 7.80) < 0.01 && Math.Abs(_cardHeightCm - 5.20) < 0.01)
        {
            if (_cardSizePresetIndex != 2) { _cardSizePresetIndex = 2; OnPropertyChanged(nameof(CardSizePresetIndex)); }
        }
        else
        {
            if (_cardSizePresetIndex != 3) { _cardSizePresetIndex = 3; OnPropertyChanged(nameof(CardSizePresetIndex)); }
        }
    }

    private double _scissorGapMm = 4.0;
    public double ScissorGapMm
    {
        get => _scissorGapMm;
        set
        {
            if (SetProperty(ref _scissorGapMm, value))
            {
                _ = UpdatePreviewAsync();
            }
        }
    }

    private double _topTrimPercent = 0.0;
    public double TopTrimPercent
    {
        get => _topTrimPercent;
        set
        {
            if (SetProperty(ref _topTrimPercent, value))
            {
                _ = RefreshExtractionAsync();
            }
        }
    }

    private double _bottomTrimPercent = 0.0;
    public double BottomTrimPercent
    {
        get => _bottomTrimPercent;
        set
        {
            if (SetProperty(ref _bottomTrimPercent, value))
            {
                _ = RefreshExtractionAsync();
            }
        }
    }

    private bool _autoWhiten = true;
    public bool AutoWhiten
    {
        get => _autoWhiten;
        set
        {
            if (SetProperty(ref _autoWhiten, value))
            {
                _ = RefreshExtractionAsync();
            }
        }
    }

    private bool _openFolderOnComplete = true;
    public bool OpenFolderOnComplete
    {
        get => _openFolderOnComplete;
        set => SetProperty(ref _openFolderOnComplete, value);
    }

    private bool _autoCopyPathToClipboard = true;
    public bool AutoCopyPathToClipboard
    {
        get => _autoCopyPathToClipboard;
        set => SetProperty(ref _autoCopyPathToClipboard, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                (ExportPdfCommand as RelayCommand)?.OnCanExecuteChanged();
                (ExportDocxCommand as RelayCommand)?.OnCanExecuteChanged();
                (ExportImageCommand as RelayCommand)?.OnCanExecuteChanged();
                (DirectPrintCommand as RelayCommand)?.OnCanExecuteChanged();
                (ReCropAllCommand as RelayCommand)?.OnCanExecuteChanged();
            }
        }
    }

    private string _statusMessage = "Add or drop e-Ration, Aadhaar, PAN, or Voter ID PDFs to begin.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private BitmapSource? _previewImage;
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (SetProperty(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreviewImage));
                OnPropertyChanged(nameof(HasNoPreviewImage));
            }
        }
    }

    public bool HasPreviewImage => PreviewImage != null;
    public bool HasNoPreviewImage => PreviewImage == null;
    public bool HasCards => Cards.Count > 0;
    public bool HasNoCards => Cards.Count == 0;

    // Pagination Properties
    private int _currentPreviewPage = 1;
    public int CurrentPreviewPage
    {
        get => _currentPreviewPage;
        set
        {
            if (SetProperty(ref _currentPreviewPage, value))
            {
                OnPropertyChanged(nameof(PageSummaryText));
                OnPropertyChanged(nameof(CanGoPrevPage));
                OnPropertyChanged(nameof(CanGoNextPage));
                (PrevPageCommand as RelayCommand)?.OnCanExecuteChanged();
                (NextPageCommand as RelayCommand)?.OnCanExecuteChanged();
                _ = UpdatePreviewAsync();
            }
        }
    }

    private int _totalPages = 1;
    public int TotalPages
    {
        get => _totalPages;
        set
        {
            if (SetProperty(ref _totalPages, value))
            {
                OnPropertyChanged(nameof(PageSummaryText));
                OnPropertyChanged(nameof(CanGoPrevPage));
                OnPropertyChanged(nameof(CanGoNextPage));
                (PrevPageCommand as RelayCommand)?.OnCanExecuteChanged();
                (NextPageCommand as RelayCommand)?.OnCanExecuteChanged();
            }
        }
    }

    public string PageSummaryText => $"Page {CurrentPreviewPage} of {TotalPages}";
    public bool CanGoPrevPage => CurrentPreviewPage > 1;
    public bool CanGoNextPage => CurrentPreviewPage < TotalPages;

    public int TotalCardCount => Cards.Count;
    public int SelectedCardCount => Cards.Count(c => c.IsSelected);
    public string SheetSummaryText => $"{SelectedCardCount} card(s) ready • {TotalPages} A4 Sheet(s) • {CardDimensionsText} • Top-to-Bottom Paper Saver";

    private string _lastExportedFile = string.Empty;
    public string LastExportedFile
    {
        get => _lastExportedFile;
        set => SetProperty(ref _lastExportedFile, value);
    }

    // Commands
    public ICommand AddFilesCommand { get; }
    public ICommand RemoveCardCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand ReCropAllCommand { get; }
    public ICommand ReCropCardCommand { get; }
    public ICommand ExportPdfCommand { get; }
    public ICommand ExportDocxCommand { get; }
    public ICommand ExportImageCommand { get; }
    public ICommand DirectPrintCommand { get; }
    public ICommand SelectInExplorerCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand SetPresetCommand { get; }

    public GovtCardExtractorViewModel()
    {
        _extractorService = GovtCardExtractorService.Instance;
        _sheetService = CardSheetGeneratorService.Instance;

        SetPresetCommand = new RelayCommand(param =>
        {
            if (param != null && int.TryParse(param.ToString(), out int pIdx))
            {
                CardSizePresetIndex = pIdx;
            }
        });

        AddFilesCommand = new RelayCommand(_ => BrowseAndAddFiles());
        RemoveCardCommand = new RelayCommand(param =>
        {
            if (param is ExtractedCardItem item)
            {
                Cards.Remove(item);
                UpdateItemIndices();
                UpdatePagination();
                _ = UpdatePreviewAsync();
            }
        });

        MoveUpCommand = new RelayCommand(param =>
        {
            if (param is ExtractedCardItem item)
            {
                int idx = Cards.IndexOf(item);
                if (idx > 0)
                {
                    Cards.Move(idx, idx - 1);
                    UpdateItemIndices();
                    _ = UpdatePreviewAsync();
                }
            }
        });

        MoveDownCommand = new RelayCommand(param =>
        {
            if (param is ExtractedCardItem item)
            {
                int idx = Cards.IndexOf(item);
                if (idx >= 0 && idx < Cards.Count - 1)
                {
                    Cards.Move(idx, idx + 1);
                    UpdateItemIndices();
                    _ = UpdatePreviewAsync();
                }
            }
        });

        ClearAllCommand = new RelayCommand(_ =>
        {
            Cards.Clear();
            PreviewImage = null;
            CurrentPreviewPage = 1;
            TotalPages = 1;
            StatusMessage = "List cleared. Add new PDFs to begin.";
            NotifyCardStats();
        });

        ReCropAllCommand = new RelayCommand(async _ => await RefreshExtractionAsync(), _ => !IsProcessing && Cards.Count > 0);

        ReCropCardCommand = new RelayCommand(async param =>
        {
            if (param is ExtractedCardItem item)
            {
                await ReExtractSingleCardAsync(item);
            }
        });

        PrevPageCommand = new RelayCommand(_ =>
        {
            if (CurrentPreviewPage > 1) CurrentPreviewPage--;
        }, _ => CanGoPrevPage);

        NextPageCommand = new RelayCommand(_ =>
        {
            if (CurrentPreviewPage < TotalPages) CurrentPreviewPage++;
        }, _ => CanGoNextPage);

        ExportPdfCommand = new RelayCommand(async _ => await ExportPdfAsync(), _ => !IsProcessing && SelectedCardCount > 0);
        ExportDocxCommand = new RelayCommand(async _ => await ExportDocxAsync(), _ => !IsProcessing && SelectedCardCount > 0);
        ExportImageCommand = new RelayCommand(async _ => await ExportImageAsync(), _ => !IsProcessing && SelectedCardCount > 0);
        DirectPrintCommand = new RelayCommand(async _ => await DirectPrintAsync(), _ => !IsProcessing && SelectedCardCount > 0);

        SelectInExplorerCommand = new RelayCommand(_ => SelectFileInExplorer());
        CopyPathCommand = new RelayCommand(_ => CopyPathToClipboard());
    }

    public async Task AddSourceFilesAsync(IEnumerable<string> filePaths)
    {
        var validFiles = filePaths.Where(f => File.Exists(f) &&
            (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
             f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
             f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
             f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))).ToList();

        if (validFiles.Count == 0) return;

        IsProcessing = true;
        StatusMessage = $"Smart auto-detecting and extracting {validFiles.Count} document(s)...";

        try
        {
            int total = validFiles.Count;
            int current = 0;

            foreach (var file in validFiles)
            {
                current++;
                StatusMessage = $"⚡ Extracting ({current}/{total}): {Path.GetFileName(file)}...";

                try
                {
                    var items = await _extractorService.ExtractCardsFromFileAsync(file, SelectedDocType, null, AutoWhiten, TopTrimPercent, BottomTrimPercent);
                    if (items != null && items.Count > 0)
                    {
                        if (System.Windows.Application.Current?.Dispatcher != null)
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                foreach (var item in items)
                                {
                                    item.ItemIndex = Cards.Count + 1;
                                    Cards.Add(item);
                                }
                                UpdateItemIndices();
                                UpdatePagination();
                            });
                        }
                        else
                        {
                            foreach (var item in items)
                            {
                                item.ItemIndex = Cards.Count + 1;
                                Cards.Add(item);
                            }
                            UpdateItemIndices();
                            UpdatePagination();
                        }

                        // Immediate progressive update: refresh live preview as each file finishes
                        await UpdatePreviewAsync();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to extract cards from {File}", file);
                }
            }

            UpdateItemIndices();
            UpdatePagination();
            StatusMessage = $"✅ All {Cards.Count} card(s) extracted across {TotalPages} A4 page(s)! Top-to-Bottom Paper Saver ready.";
            await UpdatePreviewAsync();
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void BrowseAndAddFiles()
    {
        var dlg = new WpfOpenFileDialog
        {
            Title = "Select e-Ration, Aadhaar, PAN, or Voter ID PDFs / Images",
            Filter = "Govt Document PDFs & Images (*.pdf;*.jpg;*.png)|*.pdf;*.jpg;*.jpeg;*.png|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            _ = AddSourceFilesAsync(dlg.FileNames);
        }
    }

    public async Task RefreshExtractionAsync()
    {
        if (Cards.Count == 0) return;

        var sourcePaths = Cards.Select(c => c.SourceFilePath).Distinct().Where(File.Exists).ToList();
        Cards.Clear();
        await AddSourceFilesAsync(sourcePaths);
    }

    private async Task ReExtractSingleCardAsync(ExtractedCardItem item)
    {
        if (!File.Exists(item.SourceFilePath)) return;

        IsProcessing = true;
        StatusMessage = $"Re-extracting {item.SourceFileName} as {item.DocTypeDisplayName}...";

        try
        {
            var newItem = await _extractorService.ExtractCardAsync(item.SourceFilePath, item.DocType, null, AutoWhiten, item.TopTrimPercent, item.BottomTrimPercent, item.PageIndex);
            item.FrontImagePath = newItem.FrontImagePath;
            item.BackImagePath = newItem.BackImagePath;
            await UpdatePreviewAsync();
            StatusMessage = $"✅ Re-extracted {item.SourceFileName}!";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to re-extract single card {File}", item.SourceFilePath);
            StatusMessage = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void UpdateItemIndices()
    {
        for (int i = 0; i < Cards.Count; i++)
        {
            Cards[i].ItemIndex = i + 1;
        }
        NotifyCardStats();
    }

    private void UpdatePagination()
    {
        int selCount = SelectedCardCount;
        TotalPages = Math.Max(1, _sheetService.GetTotalPages(selCount, SelectedLayout));
        if (CurrentPreviewPage > TotalPages) CurrentPreviewPage = TotalPages;
        if (CurrentPreviewPage < 1) CurrentPreviewPage = 1;
    }

    private void NotifyCardStats()
    {
        UpdatePagination();
        OnPropertyChanged(nameof(TotalCardCount));
        OnPropertyChanged(nameof(SelectedCardCount));
        OnPropertyChanged(nameof(SheetSummaryText));
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(HasNoCards));
        (ExportPdfCommand as RelayCommand)?.OnCanExecuteChanged();
        (ExportDocxCommand as RelayCommand)?.OnCanExecuteChanged();
        (ExportImageCommand as RelayCommand)?.OnCanExecuteChanged();
        (DirectPrintCommand as RelayCommand)?.OnCanExecuteChanged();
        (ReCropAllCommand as RelayCommand)?.OnCanExecuteChanged();
    }

    private async Task UpdatePreviewAsync()
    {
        var selected = Cards.Where(c => c.IsSelected && File.Exists(c.FrontImagePath)).ToList();
        if (selected.Count == 0)
        {
            PreviewImage = null;
            return;
        }

        try
        {
            string tempPreview = Path.Combine(Path.GetTempPath(), $"smartsaver_sheet_prev_{Guid.NewGuid():N}.png");
            int pageIdx = Math.Clamp(CurrentPreviewPage - 1, 0, TotalPages - 1);

            await _sheetService.GenerateImageAsync(selected, tempPreview, SelectedLayout, ScissorGapMm, SelectedAlignment, pageIdx, CardWidthCm, CardHeightCm);

            if (File.Exists(tempPreview))
            {
                byte[] imgBytes = File.ReadAllBytes(tempPreview);
                using (var ms = new MemoryStream(imgBytes))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    PreviewImage = bmp;
                }

                try { File.Delete(tempPreview); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to render card sheet live preview");
        }
    }

    private async Task ExportPdfAsync()
    {
        var selected = Cards.Where(c => c.IsSelected && File.Exists(c.FrontImagePath)).ToList();
        if (selected.Count == 0) return;

        var dlg = new WpfSaveFileDialog
        {
            Title = "Save A4 Card Print Sheet (PDF)",
            Filter = "PDF Document (*.pdf)|*.pdf",
            FileName = $"Govt_Cards_A4_Print_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
        };

        if (dlg.ShowDialog() == true)
        {
            IsProcessing = true;
            StatusMessage = "Generating vector-sharp A4 PDF with dynamic pages...";

            try
            {
                string outPath = await _sheetService.GeneratePdfAsync(selected, dlg.FileName, SelectedLayout, ScissorGapMm, SelectedAlignment, CardWidthCm, CardHeightCm);
                LastExportedFile = outPath;
                OutputHistoryService.Instance.Record(outPath, "Govt Card A4 PDF");
                StatusMessage = $"✅ {TotalPages}-Page A4 PDF Generated: {Path.GetFileName(outPath)} ({new FileInfo(outPath).Length / 1024.0:F1} KB)";

                HandlePostExport(outPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to export A4 Card PDF");
                StatusMessage = $"❌ Error: {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }

    private async Task ExportDocxAsync()
    {
        var selected = Cards.Where(c => c.IsSelected && File.Exists(c.FrontImagePath)).ToList();
        if (selected.Count == 0) return;

        var dlg = new WpfSaveFileDialog
        {
            Title = "Save A4 Card Print Sheet (Word Document)",
            Filter = "Microsoft Word Document (*.docx)|*.docx",
            FileName = $"Govt_Cards_A4_Word_{DateTime.Now:yyyyMMdd_HHmmss}.docx"
        };

        if (dlg.ShowDialog() == true)
        {
            IsProcessing = true;
            StatusMessage = $"Generating Microsoft Word (.docx) with exact {CardDimensionsText} sizing...";

            try
            {
                string outPath = await _sheetService.GenerateDocxAsync(selected, dlg.FileName, SelectedLayout, ScissorGapMm, SelectedAlignment, CardWidthCm, CardHeightCm);
                LastExportedFile = outPath;
                OutputHistoryService.Instance.Record(outPath, "Govt Card A4 Word DOCX");
                StatusMessage = $"✅ {TotalPages}-Page Word Document Generated: {Path.GetFileName(outPath)} ({new FileInfo(outPath).Length / 1024.0:F1} KB)";

                HandlePostExport(outPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to export A4 Card Word Document");
                StatusMessage = $"❌ Error: {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }

    private async Task ExportImageAsync()
    {
        var selected = Cards.Where(c => c.IsSelected && File.Exists(c.FrontImagePath)).ToList();
        if (selected.Count == 0) return;

        var dlg = new WpfSaveFileDialog
        {
            Title = "Save A4 Card Print Sheet (300 DPI Image)",
            Filter = "JPEG Image (*.jpg)|*.jpg|PNG Image (*.png)|*.png",
            FileName = (TotalPages > 1)
                ? $"Govt_Cards_A4_Page_1_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
                : $"Govt_Cards_A4_Image_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
        };

        if (dlg.ShowDialog() == true)
        {
            IsProcessing = true;
            StatusMessage = "Generating 300 DPI high-resolution A4 image sheets...";

            try
            {
                string baseDir = Path.GetDirectoryName(dlg.FileName)!;
                string baseNameWithoutExt = Path.GetFileNameWithoutExtension(dlg.FileName);
                string ext = Path.GetExtension(dlg.FileName);

                string outPath = dlg.FileName;

                for (int p = 0; p < TotalPages; p++)
                {
                    string pagePath = (TotalPages > 1)
                        ? Path.Combine(baseDir, $"{baseNameWithoutExt}_Page_{p + 1}{ext}")
                        : dlg.FileName;

                    await _sheetService.GenerateImageAsync(selected, pagePath, SelectedLayout, ScissorGapMm, SelectedAlignment, p, CardWidthCm, CardHeightCm);
                    OutputHistoryService.Instance.Record(pagePath, "Govt Card A4 Image");
                    if (p == 0) outPath = pagePath;
                }

                LastExportedFile = outPath;
                StatusMessage = $"✅ Generated {TotalPages} high-res A4 image sheet(s)!";

                HandlePostExport(outPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to export A4 Card Image");
                StatusMessage = $"❌ Error: {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }

    private async Task DirectPrintAsync()
    {
        var selected = Cards.Where(c => c.IsSelected && File.Exists(c.FrontImagePath)).ToList();
        if (selected.Count == 0) return;

        IsProcessing = true;
        StatusMessage = $"Preparing {TotalPages} A4 sheet(s) for printer...";

        try
        {
            var tempFiles = new List<string>();

            for (int p = 0; p < TotalPages; p++)
            {
                string tempPrintImg = Path.Combine(Path.GetTempPath(), $"smartsaver_print_p{p}_{Guid.NewGuid():N}.jpg");
                await _sheetService.GenerateImageAsync(selected, tempPrintImg, SelectedLayout, ScissorGapMm, SelectedAlignment, p, CardWidthCm, CardHeightCm);
                tempFiles.Add(tempPrintImg);
            }

            if (tempFiles.Count > 0)
            {
                PrintService.PrintMultipleImagesDirect(tempFiles, $"Govt ID Cards ({TotalPages} Pages)");
                StatusMessage = $"🖨️ Sent {TotalPages} A4 sheet(s) to printer!";

                foreach (var f in tempFiles)
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to direct print A4 card sheet");
            StatusMessage = $"❌ Print Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void HandlePostExport(string filePath)
    {
        if (AutoCopyPathToClipboard && !string.IsNullOrEmpty(filePath))
        {
            CopyPathToClipboard();
            StatusMessage += " • 📋 Path Copied to Clipboard!";
        }

        if (OpenFolderOnComplete && !string.IsNullOrEmpty(filePath))
        {
            SelectFileInExplorer();
        }
    }

    private void SelectFileInExplorer()
    {
        if (string.IsNullOrEmpty(LastExportedFile) || !File.Exists(LastExportedFile)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{LastExportedFile}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void CopyPathToClipboard()
    {
        if (string.IsNullOrEmpty(LastExportedFile)) return;

        try
        {
            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => WpfClipboard.SetText(LastExportedFile));
            }
            else
            {
                WpfClipboard.SetText(LastExportedFile);
            }
        }
        catch { }
    }
}
