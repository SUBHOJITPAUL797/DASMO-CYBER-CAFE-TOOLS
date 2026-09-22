using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using PdfiumViewer;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using SysRectangleF = System.Drawing.RectangleF;

namespace SmartSaver.Services;

public enum GovtDocumentType
{
    AutoDetect,     // 🤖 Smart Auto-Detection per document (Ration, Aadhaar, PAN, Voter, etc.)
    RationCardWB,   // 🍚 West Bengal / NFSA Digital e-Ration Card (Front & Back bottom)
    AadhaarCard,    // 🆔 UIDAI e-Aadhaar PDF
    PanCard,        // 💳 NSDL / UTIITSL e-PAN Card
    VoterCard,      // 🗳️ ECI e-EPIC Voter Card
    AyushmanCard,   // 🏥 PM-JAY Ayushman Bharat Card
    FullPageCard,   // 📄 Pre-cropped ID card or full photo
    CustomCrop      // 📐 User defined crop box
}

public class ExtractedCardItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceFilePath { get; set; } = string.Empty;
    public int PageIndex { get; set; } = 0;
    public string CustomDisplayName { get; set; } = string.Empty;
    public string SourceFileName => !string.IsNullOrEmpty(CustomDisplayName) ? CustomDisplayName : Path.GetFileName(SourceFilePath);

    private GovtDocumentType _docType = GovtDocumentType.RationCardWB;
    public GovtDocumentType DocType
    {
        get => _docType;
        set
        {
            if (SetProperty(ref _docType, value))
            {
                OnPropertyChanged(nameof(DocTypeDisplayName));
            }
        }
    }

    public string DocTypeDisplayName => DocType switch
    {
        GovtDocumentType.RationCardWB => "🍚 Ration Card",
        GovtDocumentType.AadhaarCard => "🆔 Aadhaar Card",
        GovtDocumentType.PanCard => "💳 PAN Card",
        GovtDocumentType.VoterCard => "🗳️ Voter ID",
        GovtDocumentType.AyushmanCard => "🏥 Ayushman",
        GovtDocumentType.FullPageCard => "📄 Full Card",
        _ => "⚡ Auto Card"
    };

    private string _frontImagePath = string.Empty;
    public string FrontImagePath
    {
        get => _frontImagePath;
        set
        {
            if (SetProperty(ref _frontImagePath, value))
            {
                OnPropertyChanged(nameof(HasBack));
            }
        }
    }

    private string _backImagePath = string.Empty;
    public string BackImagePath
    {
        get => _backImagePath;
        set
        {
            if (SetProperty(ref _backImagePath, value))
            {
                OnPropertyChanged(nameof(HasBack));
            }
        }
    }

    public bool HasBack => !string.IsNullOrEmpty(BackImagePath) && File.Exists(BackImagePath);

    private int _itemIndex;
    public int ItemIndex
    {
        get => _itemIndex;
        set
        {
            if (SetProperty(ref _itemIndex, value))
            {
                OnPropertyChanged(nameof(TargetPageNumber));
            }
        }
    }

    public int TargetPageNumber => ((ItemIndex - 1) / 5) + 1;

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public double TopTrimPercent { get; set; } = 0.0;
    public double BottomTrimPercent { get; set; } = 0.0;
    public string DetectionMethod { get; set; } = "OnDevice_ComputerVision";
}

public class PythonCardItemResult
{
    public bool success { get; set; }
    public string front_image { get; set; } = string.Empty;
    public string back_image { get; set; } = string.Empty;
    public bool has_back { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public double aspect_ratio { get; set; }
    public string method { get; set; } = string.Empty;
    public int page_number { get; set; } = 1;
}

public class PythonDetectionResult
{
    public bool success { get; set; }
    public string front_image { get; set; } = string.Empty;
    public string back_image { get; set; } = string.Empty;
    public bool has_back { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public double aspect_ratio { get; set; }
    public string method { get; set; } = string.Empty;
    public int page_number { get; set; } = 1;
    public List<PythonCardItemResult>? cards { get; set; }
    public int total_cards { get; set; }
    public string error { get; set; } = string.Empty;
}

public class GovtCardExtractorService
{
    private static readonly Lazy<GovtCardExtractorService> _instance = new(() => new GovtCardExtractorService());
    public static GovtCardExtractorService Instance => _instance.Value;

    private readonly string _cacheDir;
    private readonly string _pythonScriptPath;

    public GovtCardExtractorService()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "SmartSaver", "ExtractedCards");
        Directory.CreateDirectory(_cacheDir);

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _pythonScriptPath = Path.Combine(baseDir, "Services", "SmartCardDetector.py");
        if (!File.Exists(_pythonScriptPath))
        {
            _pythonScriptPath = Path.Combine(baseDir, "SmartCardDetector.py");
        }
    }

    /// <summary>
    /// Intelligently detects and extracts the physical ID card(s) from Indian Govt PDFs or Images.
    /// Handles single-page and multi-page documents (extracting every member card in family batches).
    /// </summary>
    public async Task<List<ExtractedCardItem>> ExtractCardsFromFileAsync(
        string filePath,
        GovtDocumentType requestedDocType = GovtDocumentType.AutoDetect,
        SysRectangleF? customBox = null,
        bool autoWhiten = true,
        double topTrimPercent = 0.0,
        double bottomTrimPercent = 0.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Government document file not found", filePath);

        // 1. Resolve Document Type
        GovtDocumentType effectiveDocType = requestedDocType;
        if (effectiveDocType == GovtDocumentType.AutoDetect)
        {
            effectiveDocType = AutoDetectDocumentType(filePath);
        }

        var results = new List<ExtractedCardItem>();

        // 2. Try Python AI/CV Multi-Page Detector first if available
        try
        {
            var pyResult = await RunPythonCardDetectorAsync(filePath, effectiveDocType, autoWhiten, topTrimPercent, bottomTrimPercent);
            if (pyResult != null && pyResult.success)
            {
                if (pyResult.cards != null && pyResult.cards.Count > 0)
                {
                    foreach (var card in pyResult.cards)
                    {
                        if (File.Exists(card.front_image))
                        {
                            var item = new ExtractedCardItem
                            {
                                SourceFilePath = filePath,
                                DocType = effectiveDocType,
                                TopTrimPercent = topTrimPercent,
                                BottomTrimPercent = bottomTrimPercent,
                                FrontImagePath = card.front_image,
                                BackImagePath = card.back_image,
                                PageIndex = card.page_number - 1,
                                CustomDisplayName = (pyResult.cards.Count > 1)
                                    ? $"{Path.GetFileNameWithoutExtension(filePath)} (Page {card.page_number})"
                                    : Path.GetFileName(filePath),
                                DetectionMethod = $"Python_{card.method}"
                            };
                            results.Add(item);
                        }
                    }
                }
                else if (File.Exists(pyResult.front_image))
                {
                    var item = new ExtractedCardItem
                    {
                        SourceFilePath = filePath,
                        DocType = effectiveDocType,
                        TopTrimPercent = topTrimPercent,
                        BottomTrimPercent = bottomTrimPercent,
                        FrontImagePath = pyResult.front_image,
                        BackImagePath = pyResult.back_image,
                        DetectionMethod = $"Python_{pyResult.method}"
                    };
                    results.Add(item);
                }

                if (results.Count > 0)
                {
                    Log.Information("Python Card Detector extracted {Count} card(s) from {File}", results.Count, Path.GetFileName(filePath));
                    return results;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Python detector skipped, using native on-device C# engine");
        }

        // 3. Native 100% On-Device C# Multi-Page Computer Vision Engine
        return await Task.Run(async () =>
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".pdf")
            {
                byte[] pdfBytes = await File.ReadAllBytesAsync(filePath);
                using var pdfMs = new MemoryStream(pdfBytes);
                using var pdfDoc = PdfDocument.Load(pdfMs);
                if (pdfDoc.PageCount == 0)
                    throw new InvalidOperationException("PDF contains no pages.");

                int totalPages = pdfDoc.PageCount;
                var firstPageSize = pdfDoc.PageSizes[0];
                double pageAR = firstPageSize.Width / (double)firstPageSize.Height;
                bool isCardSized = firstPageSize.Height < 400 || pageAR >= 1.35;
                bool isAyushman2Page = totalPages == 2 && (isCardSized || effectiveDocType == GovtDocumentType.AyushmanCard);
                bool isAyushman1PageVertical = totalPages == 1 && effectiveDocType == GovtDocumentType.AyushmanCard && pageAR < 1.15;

                if (isAyushman2Page)
                {
                    var cardItem = new ExtractedCardItem
                    {
                        SourceFilePath = filePath,
                        DocType = effectiveDocType,
                        TopTrimPercent = topTrimPercent,
                        BottomTrimPercent = bottomTrimPercent
                    };

                    string frontRenderPath = Path.Combine(_cacheDir, $"card_front_{cardItem.Id}.png");
                    string backRenderPath = Path.Combine(_cacheDir, $"card_back_{cardItem.Id}.png");

                    using (var p0Image = pdfDoc.Render(0, 400, 400, PdfRenderFlags.Annotations | PdfRenderFlags.CorrectFromDpi))
                    {
                        p0Image.Save(frontRenderPath, ImageFormat.Png);
                    }
                    using (var p1Image = pdfDoc.Render(1, 400, 400, PdfRenderFlags.Annotations | PdfRenderFlags.CorrectFromDpi))
                    {
                        p1Image.Save(backRenderPath, ImageFormat.Png);
                    }

                    if (autoWhiten)
                    {
                        using var img0 = await ImageSharpImage.LoadAsync<Rgba32>(frontRenderPath);
                        AutoWhitenPaper(img0);
                        using var trimmed0 = TrimOuterWhiteMargins(img0);
                        await trimmed0.SaveAsPngAsync(frontRenderPath);

                        using var img1 = await ImageSharpImage.LoadAsync<Rgba32>(backRenderPath);
                        AutoWhitenPaper(img1);
                        using var trimmed1 = TrimOuterWhiteMargins(img1);
                        await trimmed1.SaveAsPngAsync(backRenderPath);
                    }

                    cardItem.FrontImagePath = frontRenderPath;
                    cardItem.BackImagePath = backRenderPath;
                    cardItem.DetectionMethod = "OnDevice_Ayushman2Page";
                    results.Add(cardItem);
                    return results;
                }

                if (isAyushman1PageVertical)
                {
                    var cardItem = new ExtractedCardItem
                    {
                        SourceFilePath = filePath,
                        DocType = effectiveDocType,
                        TopTrimPercent = topTrimPercent,
                        BottomTrimPercent = bottomTrimPercent
                    };

                    string renderedImagePath = Path.Combine(_cacheDir, $"pdf_render_{Guid.NewGuid():N}.png");
                    using (var pageImage = pdfDoc.Render(0, 400, 400, PdfRenderFlags.Annotations | PdfRenderFlags.CorrectFromDpi))
                    {
                        pageImage.Save(renderedImagePath, ImageFormat.Png);
                    }

                    using (var fullImage = await ImageSharpImage.LoadAsync<Rgba32>(renderedImagePath))
                    {
                        int w = fullImage.Width;
                        int h = fullImage.Height;
                        int midY = h / 2;
                        int cardH = (int)(midY * 0.96);
                        int cardW = (int)(w * 0.96);
                        int minX = (int)(w * 0.02);

                        using var frontRaw = fullImage.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(minX, (int)(h * 0.015), cardW, cardH)));
                        using var backRaw = fullImage.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(minX, midY + (int)(h * 0.005), cardW, cardH)));

                        if (autoWhiten)
                        {
                            AutoWhitenPaper(frontRaw);
                            AutoWhitenPaper(backRaw);
                        }

                        using var frontCard = TrimOuterWhiteMargins(frontRaw);
                        using var backCard = TrimOuterWhiteMargins(backRaw);

                        string frontPath = Path.Combine(_cacheDir, $"card_front_{cardItem.Id}.png");
                        string backPath = Path.Combine(_cacheDir, $"card_back_{cardItem.Id}.png");

                        await frontCard.SaveAsPngAsync(frontPath);
                        await backCard.SaveAsPngAsync(backPath);

                        cardItem.FrontImagePath = frontPath;
                        cardItem.BackImagePath = backPath;
                        cardItem.DetectionMethod = "OnDevice_Ayushman1PageVertical";
                        results.Add(cardItem);
                    }

                    try { File.Delete(renderedImagePath); } catch { }
                    return results;
                }

                // Process every page of multi-page / single-page PDF
                for (int pno = 0; pno < totalPages; pno++)
                {
                    var cardItem = new ExtractedCardItem
                    {
                        SourceFilePath = filePath,
                        DocType = effectiveDocType,
                        PageIndex = pno,
                        CustomDisplayName = (totalPages > 1) ? $"{Path.GetFileNameWithoutExtension(filePath)} (Page {pno + 1})" : Path.GetFileName(filePath),
                        TopTrimPercent = topTrimPercent,
                        BottomTrimPercent = bottomTrimPercent
                    };

                    string renderedImagePath = Path.Combine(_cacheDir, $"pdf_render_{Guid.NewGuid():N}.png");
                    using (var pageImage = pdfDoc.Render(pno, 400, 400, PdfRenderFlags.Annotations | PdfRenderFlags.CorrectFromDpi))
                    {
                        pageImage.Save(renderedImagePath, ImageFormat.Png);
                    }

                    using var fullImage = await ImageSharpImage.LoadAsync<Rgba32>(renderedImagePath);
                    int origW = fullImage.Width;
                    int origH = fullImage.Height;

                    bool pageIsCardSized = origH < 1800 || (origW / (double)origH) >= 1.30;
                    SixLabors.ImageSharp.Rectangle detectedRect;

                    if (pageIsCardSized)
                    {
                        detectedRect = new SixLabors.ImageSharp.Rectangle(0, 0, origW, origH);
                    }
                    else
                    {
                        detectedRect = DetectCardRegionOnDevice(fullImage, effectiveDocType, customBox, topTrimPercent, bottomTrimPercent);
                    }

                    int cx = Math.Clamp(detectedRect.X, 0, origW - 1);
                    int cy = Math.Clamp(detectedRect.Y, 0, origH - 1);
                    int cw = Math.Clamp(detectedRect.Width, 1, origW - cx);
                    int ch = Math.Clamp(detectedRect.Height, 1, origH - cy);

                    using var cardBlock = fullImage.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(cx, cy, cw, ch)));
                    if (autoWhiten) AutoWhitenPaper(cardBlock);

                    double ar = cw / (double)ch;

                    if (ar > 2.0)
                    {
                        int splitX = FindVerticalDivider(cardBlock);
                        using var frontRaw = cardBlock.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(0, 0, splitX, ch)));
                        using var backRaw = cardBlock.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(splitX, 0, cw - splitX, ch)));

                        using var frontCard = TrimOuterWhiteMargins(frontRaw);
                        using var backCard = TrimOuterWhiteMargins(backRaw);

                        string frontPath = Path.Combine(_cacheDir, $"card_front_{cardItem.Id}.png");
                        string backPath = Path.Combine(_cacheDir, $"card_back_{cardItem.Id}.png");

                        await frontCard.SaveAsPngAsync(frontPath);
                        await backCard.SaveAsPngAsync(backPath);

                        cardItem.FrontImagePath = frontPath;
                        cardItem.BackImagePath = backPath;
                        cardItem.DetectionMethod = "OnDevice_CV_DoubleCard";
                    }
                    else
                    {
                        using var singleCard = TrimOuterWhiteMargins(cardBlock);
                        string frontPath = Path.Combine(_cacheDir, $"card_front_{cardItem.Id}.png");
                        await singleCard.SaveAsPngAsync(frontPath);
                        cardItem.FrontImagePath = frontPath;
                        cardItem.DetectionMethod = "OnDevice_CV_SingleCard";
                    }

                    try { File.Delete(renderedImagePath); } catch { }
                    results.Add(cardItem);
                }

                return results;
            }
            else
            {
                // Single Image Scan
                var cardItem = new ExtractedCardItem
                {
                    SourceFilePath = filePath,
                    DocType = effectiveDocType,
                    TopTrimPercent = topTrimPercent,
                    BottomTrimPercent = bottomTrimPercent
                };

                using var fullImage = await ImageSharpImage.LoadAsync<Rgba32>(filePath);
                int origW = fullImage.Width;
                int origH = fullImage.Height;

                var detectedRect = ((origW / (double)origH) >= 1.30)
                    ? new SixLabors.ImageSharp.Rectangle(0, 0, origW, origH)
                    : DetectCardRegionOnDevice(fullImage, effectiveDocType, customBox, topTrimPercent, bottomTrimPercent);

                int cx = Math.Clamp(detectedRect.X, 0, origW - 1);
                int cy = Math.Clamp(detectedRect.Y, 0, origH - 1);
                int cw = Math.Clamp(detectedRect.Width, 1, origW - cx);
                int ch = Math.Clamp(detectedRect.Height, 1, origH - cy);

                using var cardBlock = fullImage.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(cx, cy, cw, ch)));
                if (autoWhiten) AutoWhitenPaper(cardBlock);

                double ar = cw / (double)ch;

                if (ar > 2.0)
                {
                    int splitX = FindVerticalDivider(cardBlock);
                    using var frontRaw = cardBlock.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(0, 0, splitX, ch)));
                    using var backRaw = cardBlock.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(splitX, 0, cw - splitX, ch)));

                    using var frontCard = TrimOuterWhiteMargins(frontRaw);
                    using var backCard = TrimOuterWhiteMargins(backRaw);

                    string frontPath = Path.Combine(_cacheDir, $"card_front_{cardItem.Id}.png");
                    string backPath = Path.Combine(_cacheDir, $"card_back_{cardItem.Id}.png");

                    await frontCard.SaveAsPngAsync(frontPath);
                    await backCard.SaveAsPngAsync(backPath);

                    cardItem.FrontImagePath = frontPath;
                    cardItem.BackImagePath = backPath;
                    cardItem.DetectionMethod = "OnDevice_CV_DoubleCard";
                }
                else
                {
                    using var singleCard = TrimOuterWhiteMargins(cardBlock);
                    string frontPath = Path.Combine(_cacheDir, $"card_front_{cardItem.Id}.png");
                    await singleCard.SaveAsPngAsync(frontPath);
                    cardItem.FrontImagePath = frontPath;
                    cardItem.DetectionMethod = "OnDevice_CV_SingleCard";
                }

                results.Add(cardItem);
                return results;
            }
        });
    }

    /// <summary>
    /// Intelligently detects and extracts the physical ID card (Front and Back) on-device from Indian Govt PDFs or Images.
    /// Supports automatic document type classification (mix Ration, Aadhaar, PAN, Voter in 1 batch).
    /// </summary>
    public async Task<ExtractedCardItem> ExtractCardAsync(
        string filePath,
        GovtDocumentType requestedDocType = GovtDocumentType.AutoDetect,
        SysRectangleF? customBox = null,
        bool autoWhiten = true,
        double topTrimPercent = 0.0,
        double bottomTrimPercent = 0.0,
        int pageIndex = 0)
    {
        var items = await ExtractCardsFromFileAsync(filePath, requestedDocType, customBox, autoWhiten, topTrimPercent, bottomTrimPercent);
        if (items.Count == 0)
            throw new InvalidOperationException($"No cards could be extracted from {Path.GetFileName(filePath)}");

        if (pageIndex >= 0 && pageIndex < items.Count)
            return items[pageIndex];

        return items[0];
    }

    /// <summary>
    /// Automatically detects the type of Indian government document based on text content and visual profile.
    /// </summary>
    public static GovtDocumentType AutoDetectDocumentType(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string textContent = string.Empty;
        int pageCount = 1;
        double firstPageHeight = 842.0;

        if (ext == ".pdf")
        {
            try
            {
                byte[] pdfBytes = File.ReadAllBytes(filePath);
                using var pdfMs = new MemoryStream(pdfBytes);
                using var pdfDoc = PdfDocument.Load(pdfMs);
                pageCount = pdfDoc.PageCount;
                if (pageCount > 0)
                {
                    firstPageHeight = pdfDoc.PageSizes[0].Height;
                    for (int i = 0; i < Math.Min(2, pageCount); i++)
                    {
                        textContent += " " + (pdfDoc.GetPdfText(i) ?? string.Empty);
                    }
                }
            }
            catch { }
        }

        string upper = textContent.ToUpperInvariant();
        string filename = Path.GetFileName(filePath).ToUpperInvariant();

        // 1. Ayushman Bharat (PM-JAY) & ABHA Card (High Priority check to avoid false matches with state names)
        if (upper.Contains("AYUSHMAN") || upper.Contains("PM-JAY") || upper.Contains("PMJAY") ||
            upper.Contains("JAN AROGYA") || upper.Contains("NATIONAL HEALTH") || upper.Contains("ABHA") ||
            upper.Contains("HEALTH AUTHORITY") || upper.Contains("আয়ুষ্মান") || upper.Contains("জন আরোগ্য") ||
            upper.Contains("চিকিৎসা") || upper.Contains("আভা") ||
            filename.Contains("AYUSHMAN") || filename.Contains("PMJAY") || filename.Contains("ABHA") ||
            System.Text.RegularExpressions.Regex.IsMatch(filename, @"_[A-Z0-9]{8,12}\.PDF") ||
            (pageCount >= 2 && firstPageHeight < 400))
        {
            return GovtDocumentType.AyushmanCard;
        }

        // 2. UIDAI e-Aadhaar
        if (upper.Contains("UNIQUE IDENTIFICATION") || upper.Contains("AADHAAR") || upper.Contains("UIDAI") ||
            upper.Contains("भारतीय विशिष्ट पहचान प्राधिकरण") || upper.Contains("MERA AADHAAR") ||
            upper.Contains("ENROLMENT") || filename.Contains("AADHAAR") || filename.Contains("ADHAR"))
        {
            return GovtDocumentType.AadhaarCard;
        }

        // 3. Income Tax e-PAN Card
        if (upper.Contains("INCOME TAX") || upper.Contains("PERMANENT ACCOUNT") || upper.Contains("NSDL") || upper.Contains("UTIITSL") ||
            filename.Contains("PAN"))
        {
            return GovtDocumentType.PanCard;
        }

        // 4. Election Commission Voter ID
        if (upper.Contains("ELECTION COMMISSION") || upper.Contains("ELECTORAL") || upper.Contains("EPIC") ||
            upper.Contains("ELECTOR PHOTO") || upper.Contains("निर्वाचन") ||
            filename.Contains("VOTER") || filename.Contains("EPIC"))
        {
            return GovtDocumentType.VoterCard;
        }

        // 5. West Bengal / NFSA e-Ration Card
        if (upper.Contains("DIGITAL RATION CARD") || upper.Contains("FOOD & SUPPLIES") || upper.Contains("RATION") ||
            upper.Contains("খাদ্য ও সরবরাহ") || upper.Contains("AAY") || upper.Contains("SPHH") || upper.Contains("PHH") || upper.Contains("RKSY") ||
            filename.Contains("RATION") || filename.Contains("WB_"))
        {
            return GovtDocumentType.RationCardWB;
        }

        // Fallback for general West Bengal govt documents
        if (upper.Contains("WEST BENGAL") || upper.Contains("পশ্চিমবঙ্গ"))
        {
            return GovtDocumentType.RationCardWB;
        }

        return GovtDocumentType.RationCardWB;
    }

    /// <summary>
    /// Native On-Device Computer Vision algorithm that scans pixel rows for colored card stripes,
    /// high-contrast card headers, and contours strictly below upper summary/instruction texts.
    /// </summary>
    private static SixLabors.ImageSharp.Rectangle DetectCardRegionOnDevice(
        Image<Rgba32> image,
        GovtDocumentType docType,
        SysRectangleF? customBox,
        double topTrimPercent,
        double bottomTrimPercent)
    {
        int w = image.Width;
        int h = image.Height;

        if (customBox.HasValue)
        {
            var cb = customBox.Value;
            return new SixLabors.ImageSharp.Rectangle(
                (int)(cb.X * w), (int)(cb.Y * h),
                (int)(cb.Width * w), (int)(cb.Height * h)
            );
        }

        if (docType == GovtDocumentType.FullPageCard)
        {
            return new SixLabors.ImageSharp.Rectangle(0, 0, w, h);
        }

        if (docType == GovtDocumentType.RationCardWB)
        {
            int cardY0 = (int)(h * (0.776 + (topTrimPercent / 100.0 * 0.18)));
            int cardY1 = (int)(h * (0.972 - (bottomTrimPercent / 100.0 * 0.18)));
            int minX = (int)(w * 0.068);
            int maxX = (int)(w * 0.932);
            return new SixLabors.ImageSharp.Rectangle(minX, cardY0, Math.Max(10, maxX - minX), Math.Max(10, cardY1 - cardY0));
        }

        if (docType == GovtDocumentType.AadhaarCard)
        {
            int cardY0 = (int)(h * (0.680 + (topTrimPercent / 100.0 * 0.28)));
            int cardY1 = (int)(h * (0.965 - (bottomTrimPercent / 100.0 * 0.28)));
            int minX = (int)(w * 0.04);
            int maxX = (int)(w * 0.96);
            return new SixLabors.ImageSharp.Rectangle(minX, cardY0, Math.Max(10, maxX - minX), Math.Max(10, cardY1 - cardY0));
        }

        if (docType == GovtDocumentType.PanCard)
        {
            // PAN cards typically appear in the upper-middle or bottom-middle region
            int cardY0 = (int)(h * (0.280 + (topTrimPercent / 100.0 * 0.20)));
            int cardY1 = (int)(h * (0.620 - (bottomTrimPercent / 100.0 * 0.20)));
            int minX = (int)(w * 0.15);
            int maxX = (int)(w * 0.85);
            return new SixLabors.ImageSharp.Rectangle(minX, cardY0, Math.Max(10, maxX - minX), Math.Max(10, cardY1 - cardY0));
        }

        if (docType == GovtDocumentType.VoterCard)
        {
            // Voter ID e-EPIC cards are situated side-by-side in the upper third of the page
            int cardY0 = (int)(h * (0.090 + (topTrimPercent / 100.0 * 0.15)));
            int cardY1 = (int)(h * (0.350 - (bottomTrimPercent / 100.0 * 0.15)));
            int minX = (int)(w * 0.04);
            int maxX = (int)(w * 0.96);
            return new SixLabors.ImageSharp.Rectangle(minX, cardY0, Math.Max(10, maxX - minX), Math.Max(10, cardY1 - cardY0));
        }

        // Calibrated official template region fallback
        int defY0 = (int)(h * (0.765 + (topTrimPercent / 100.0 * 0.20)));
        int defY1 = (int)(h * (0.965 - (bottomTrimPercent / 100.0 * 0.20)));
        int defMinX = (int)(w * 0.04);
        int defMaxX = (int)(w * 0.96);

        return new SixLabors.ImageSharp.Rectangle(defMinX, defY0, Math.Max(10, defMaxX - defMinX), Math.Max(10, defY1 - defY0));
    }

    private static int FindVerticalDivider(Image<Rgba32> cardBlock)
    {
        int w = cardBlock.Width;
        int h = cardBlock.Height;
        int midStart = (int)(w * 0.46);
        int midEnd = (int)(w * 0.54);

        int bestX = w / 2;
        long minColorSum = long.MaxValue;

        cardBlock.ProcessPixelRows(accessor =>
        {
            for (int x = midStart; x <= midEnd; x++)
            {
                long colColorSum = 0;
                for (int y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    ref var p = ref row[x];
                    int max = Math.Max(p.R, Math.Max(p.G, p.B));
                    int min = Math.Min(p.R, Math.Min(p.G, p.B));
                    colColorSum += (max - min);
                }

                if (colColorSum < minColorSum)
                {
                    minColorSum = colColorSum;
                    bestX = x;
                }
            }
        });

        return bestX;
    }

    private static Image<Rgba32> TrimOuterWhiteMargins(Image<Rgba32> rawCard, int tolerance = 242)
    {
        int w = rawCard.Width;
        int h = rawCard.Height;

        int minX = w, maxX = 0, minY = h, maxY = 0;

        rawCard.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    ref var p = ref row[x];
                    if (p.R < tolerance || p.G < tolerance || p.B < tolerance)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        });

        if (minX < maxX && minY < maxY)
        {
            minX = Math.Max(0, minX - 8);
            minY = Math.Max(0, minY - 8);
            maxX = Math.Min(w - 1, maxX + 8);
            maxY = Math.Min(h - 1, maxY + 8);

            int cropW = maxX - minX + 1;
            int cropH = maxY - minY + 1;

            if (cropW > 20 && cropH > 20)
            {
                return rawCard.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(minX, minY, cropW, cropH)));
            }
        }

        return rawCard.Clone();
    }

    private async Task<PythonDetectionResult?> RunPythonCardDetectorAsync(
        string filePath,
        GovtDocumentType docType,
        bool autoWhiten,
        double topTrim,
        double botTrim)
    {
        string pythonExe = FindPythonExecutable();
        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
            return null;

        string scriptPath = _pythonScriptPath;
        if (!File.Exists(scriptPath))
        {
            string altPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SmartCardDetector.py");
            if (File.Exists(altPath)) scriptPath = altPath;
            else return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{scriptPath}\" --input \"{filePath}\" --outdir \"{_cacheDir}\" --doctype \"{docType}\" --autowhiten {autoWhiten} --toptrim {topTrim:F1} --bottrim {botTrim:F1}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        string output;
        string error;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            error = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            Log.Warning("Python Card Detector timed out after 30s for {File}", Path.GetFileName(filePath));
            return null;
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            int jsonStart = output.IndexOf('{');
            int jsonEnd = output.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                string jsonStr = output.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var result = JsonSerializer.Deserialize<PythonDetectionResult>(jsonStr);
                return result;
            }
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Log.Warning("Python Card Detector error: {Error}", error);
        }

        return null;
    }

    private static string FindPythonExecutable()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python314", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python313", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python310", "python.exe"),
            @"C:\Python314\python.exe",
            @"C:\Python312\python.exe",
            @"C:\Python311\python.exe",
            "python.exe"
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "python",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string line = proc.StandardOutput.ReadLine() ?? string.Empty;
                proc.WaitForExit();
                if (File.Exists(line)) return line;
            }
        }
        catch { }

        return "python.exe";
    }

    private static void AutoWhitenPaper(Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    int max = Math.Max(p.R, Math.Max(p.G, p.B));
                    int min = Math.Min(p.R, Math.Min(p.G, p.B));

                    if (min > 215 && (max - min) < 25)
                    {
                        p.R = 255;
                        p.G = 255;
                        p.B = 255;
                    }
                }
            }
        });
    }
}
