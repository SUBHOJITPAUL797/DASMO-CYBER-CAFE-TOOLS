using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Serilog;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace SmartSaver.Services;

public enum CardSheetLayout
{
    FiveCardsA4PaperSaver,  // 5 Cards (10 sides) per A4 sheet (Front left, Back right)
    TwoCardsA4,             // 2 Cards (4 sides) per A4 sheet
    SingleCardSideBySide,   // 1 Card (2 sides) side-by-side with fold line
    SingleCardTopBottom     // 1 Card (2 sides) top and bottom stack
}

public enum CardSheetAlignment
{
    TopToBottomPaperSaver,  // Starts from top edge downwards (allows cutting & reusing bottom blank A4 paper)
    CenterOfPage            // Centered vertically on A4 page
}

public class CardSheetGeneratorService
{
    private static readonly Lazy<CardSheetGeneratorService> _instance = new(() => new CardSheetGeneratorService());
    public static CardSheetGeneratorService Instance => _instance.Value;

    // Standard card pouch dimensions: 8.00 cm x 5.40 cm (Cyber cafe / wallet standard)
    public const double CardWidthCm = 8.00;
    public const double CardHeightCm = 5.40;

    // 1 cm = 28.3464567 PDF Points (72 DPI)
    public const double CmToPoints = 28.3464567;
    // 1 cm = 360,000 OpenXML EMUs
    public const long CmToEmus = 360000;

    public int GetCardsPerPage(CardSheetLayout layout) => layout switch
    {
        CardSheetLayout.FiveCardsA4PaperSaver => 5,
        CardSheetLayout.TwoCardsA4 => 2,
        _ => 1
    };

    public int GetTotalPages(int cardCount, CardSheetLayout layout)
    {
        if (cardCount <= 0) return 1;
        int perPage = GetCardsPerPage(layout);
        return (int)Math.Ceiling((double)cardCount / perPage);
    }

    /// <summary>
    /// Generates a print-ready vector A4 PDF with dynamic multi-page overflow, minimal top margin, and top-to-bottom paper saving.
    /// </summary>
    public async Task<string> GeneratePdfAsync(
        IReadOnlyList<ExtractedCardItem> cards,
        string outputPath,
        CardSheetLayout layout = CardSheetLayout.FiveCardsA4PaperSaver,
        double scissorGapMm = 4.0,
        CardSheetAlignment alignment = CardSheetAlignment.TopToBottomPaperSaver,
        double cardWidthCm = CardWidthCm,
        double cardHeightCm = CardHeightCm)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return await Task.Run(() =>
        {
            var validCards = cards.Where(c => c.IsSelected && File.Exists(c.FrontImagePath)).ToList();
            if (validCards.Count == 0)
                throw new InvalidOperationException("No valid card items to generate sheet.");

            int cardsPerPage = GetCardsPerPage(layout);
            using var doc = new PdfDocument();
            int totalPages = (int)Math.Ceiling((double)validCards.Count / cardsPerPage);

            double cardW_pt = cardWidthCm * CmToPoints;
            double cardH_pt = cardHeightCm * CmToPoints;
            double gap_pt = (scissorGapMm / 10.0) * CmToPoints;

            var guidePen = new XPen(XColor.FromArgb(200, 200, 200), 0.5) { DashStyle = XDashStyle.Dash };
            var foldPen = new XPen(XColor.FromArgb(210, 210, 210), 0.5) { DashStyle = XDashStyle.Dot };
            var font = new XFont("Arial", 7, XFontStyle.Regular);

            for (int p = 0; p < totalPages; p++)
            {
                var page = doc.AddPage();
                page.Size = PdfSharpCore.PageSize.A4;
                double a4W = page.Width.Point;
                double a4H = page.Height.Point;

                using var gfx = XGraphics.FromPdfPage(page);
                var pageCards = validCards.Skip(p * cardsPerPage).Take(cardsPerPage).ToList();
                int rowCount = pageCards.Count;

                if (layout == CardSheetLayout.FiveCardsA4PaperSaver || layout == CardSheetLayout.TwoCardsA4)
                {
                    double totalBlockH = (rowCount * cardH_pt) + ((rowCount - 1) * gap_pt);
                    double totalBlockW = (2 * cardW_pt) + gap_pt;
                    double startX = Math.Max(10, (a4W - totalBlockW) / 2.0);

                    // Top-to-Bottom Packing: start at minimal top margin (~3.5mm = 10pt) so remaining bottom page can be cut & reused!
                    double startY = (alignment == CardSheetAlignment.TopToBottomPaperSaver)
                        ? 10.0
                        : Math.Max(15, (a4H - totalBlockH) / 2.0);

                    for (int r = 0; r < rowCount; r++)
                    {
                        var card = pageCards[r];
                        double curY = startY + r * (cardH_pt + gap_pt);
                        double frontX = startX;
                        double backX = startX + cardW_pt + gap_pt;

                        // Draw Front Card
                        DrawImageExact(gfx, card.FrontImagePath, frontX, curY, cardW_pt, cardH_pt);
                        gfx.DrawRectangle(new XPen(XColor.FromArgb(215, 215, 215), 0.5), frontX, curY, cardW_pt, cardH_pt);

                        // Draw Back Card if available
                        if (card.HasBack)
                        {
                            DrawImageExact(gfx, card.BackImagePath, backX, curY, cardW_pt, cardH_pt);
                            gfx.DrawRectangle(new XPen(XColor.FromArgb(215, 215, 215), 0.5), backX, curY, cardW_pt, cardH_pt);
                        }

                        // Subtle horizontal scissor cutting guideline between card rows (NO TEXT)
                        if (r < rowCount - 1)
                        {
                            double cutY = curY + cardH_pt + (gap_pt / 2.0);
                            gfx.DrawLine(guidePen, 15, cutY, a4W - 15, cutY);
                        }
                    }

                    // Vertical center fold / cut line between Front and Back (NO TEXT)
                    double vertCutX = startX + cardW_pt + (gap_pt / 2.0);
                    gfx.DrawLine(foldPen, vertCutX, startY - 2, vertCutX, startY + totalBlockH + 2);

                    // If rowCount < 5 and TopToBottom mode: Draw clean dashed boundary for paper reuse (NO TEXT)
                    if (alignment == CardSheetAlignment.TopToBottomPaperSaver && rowCount < 5)
                    {
                        double finalCutY = startY + totalBlockH + 8;
                        if (finalCutY < a4H - 30)
                        {
                            var heavyPen = new XPen(XColor.FromArgb(160, 160, 160), 0.6) { DashStyle = XDashStyle.Dash };
                            gfx.DrawLine(heavyPen, 10, finalCutY, a4W - 10, finalCutY);
                        }
                    }
                }
                else if (layout == CardSheetLayout.SingleCardSideBySide)
                {
                    var card = pageCards[0];
                    double totalBlockW = (2 * cardW_pt) + gap_pt;
                    double startX = (a4W - totalBlockW) / 2.0;
                    double startY = (alignment == CardSheetAlignment.TopToBottomPaperSaver) ? 10.0 : (a4H - cardH_pt) / 2.0;

                    DrawImageExact(gfx, card.FrontImagePath, startX, startY, cardW_pt, cardH_pt);
                    if (card.HasBack)
                    {
                        DrawImageExact(gfx, card.BackImagePath, startX + cardW_pt + gap_pt, startY, cardW_pt, cardH_pt);
                    }

                    double vertCutX = startX + cardW_pt + (gap_pt / 2.0);
                    gfx.DrawLine(foldPen, vertCutX, startY - 4, vertCutX, startY + cardH_pt + 4);

                    if (alignment == CardSheetAlignment.TopToBottomPaperSaver)
                    {
                        double finalCutY = startY + cardH_pt + 10;
                        gfx.DrawLine(guidePen, 10, finalCutY, a4W - 10, finalCutY);
                    }
                }
                else // SingleCardTopBottom
                {
                    var card = pageCards[0];
                    double startX = (a4W - cardW_pt) / 2.0;
                    double topY = (alignment == CardSheetAlignment.TopToBottomPaperSaver) ? 10.0 : a4H * 0.16;
                    double bottomY = topY + cardH_pt + gap_pt + 6;

                    DrawImageExact(gfx, card.FrontImagePath, startX, topY, cardW_pt, cardH_pt);
                    if (card.HasBack)
                    {
                        DrawImageExact(gfx, card.BackImagePath, startX, bottomY, cardW_pt, cardH_pt);
                    }

                    double sepY = topY + cardH_pt + (gap_pt / 2.0) + 3;
                    gfx.DrawLine(guidePen, 30, sepY, a4W - 30, sepY);

                    if (alignment == CardSheetAlignment.TopToBottomPaperSaver)
                    {
                        double finalCutY = bottomY + cardH_pt + 10;
                        gfx.DrawLine(guidePen, 10, finalCutY, a4W - 10, finalCutY);
                    }
                }

                // Page Number footer if multi-page
                if (totalPages > 1)
                {
                    gfx.DrawString($"Page {p + 1} of {totalPages} • DASMO Cyber Compressor", font, XBrushes.LightGray, new XPoint(a4W - 160, a4H - 12));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            doc.Save(outputPath);
            return outputPath;
        });
    }

    /// <summary>
    /// Generates an editable Microsoft Word document (.docx) with exact physical card sizing, fixed table grid, zero cell padding, and print-ready scissor guidelines.
    /// </summary>
    public async Task<string> GenerateDocxAsync(
        IReadOnlyList<ExtractedCardItem> cards,
        string outputPath,
        CardSheetLayout layout = CardSheetLayout.FiveCardsA4PaperSaver,
        double scissorGapMm = 4.0,
        CardSheetAlignment alignment = CardSheetAlignment.TopToBottomPaperSaver,
        double cardWidthCm = CardWidthCm,
        double cardHeightCm = CardHeightCm)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return await Task.Run(() =>
        {
            var validCards = cards.Where(c => c.IsSelected && File.Exists(c.FrontImagePath)).ToList();
            if (validCards.Count == 0)
                throw new InvalidOperationException("No valid card items to generate DOCX.");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            int cardsPerPage = GetCardsPerPage(layout);
            int totalPages = (int)Math.Ceiling((double)validCards.Count / cardsPerPage);

            UInt32Value a4WidthDxa = 11906;   // 210 mm standard A4
            UInt32Value a4HeightDxa = 16838;  // 297 mm standard A4
            // Minimal top margin (144 twips = ~2.5mm) in TopToBottom Paper Saver mode
            int topMarginVal = (alignment == CardSheetAlignment.TopToBottomPaperSaver) ? 144 : 576;

            // Exact unit math: 1 cm = 566.929134 dxa (twips), 1 cm = 360,000 EMUs
            int cardW_dxa = (int)Math.Round(cardWidthCm * 566.929134);
            int cardH_dxa = (int)Math.Round(cardHeightCm * 566.929134);
            int gap_dxa = (int)Math.Round((scissorGapMm / 10.0) * 566.929134);
            long emuWidth = (long)Math.Round(cardWidthCm * CmToEmus);
            long emuHeight = (long)Math.Round(cardHeightCm * CmToEmus);

            using (var wordDoc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document))
            {
                var mainPart = wordDoc.AddMainDocumentPart();
                mainPart.Document = new Document();
                var body = mainPart.Document.AppendChild(new Body());

                var sectionProps = new SectionProperties();
                var pageSize = new PageSize { Width = a4WidthDxa, Height = a4HeightDxa, Orient = PageOrientationValues.Portrait };
                var pageMargin = new PageMargin { Top = topMarginVal, Bottom = 200, Left = 288, Right = 288 };
                sectionProps.Append(pageSize);
                sectionProps.Append(pageMargin);

                long a4W_emu = (long)Math.Round(21.0 * CmToEmus);
                long a4H_emu = (long)Math.Round(29.7 * CmToEmus);
                long emuGap = (long)Math.Round((scissorGapMm / 10.0) * CmToEmus);

                uint docPropId = 1;

                for (int p = 0; p < totalPages; p++)
                {
                    var pageCards = validCards.Skip(p * cardsPerPage).Take(cardsPerPage).ToList();
                    int rowCount = pageCards.Count;

                    var pageParagraph = new Paragraph(new ParagraphProperties(
                        new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }
                    ));

                    if (layout == CardSheetLayout.SingleCardTopBottom)
                    {
                        long startX = Math.Max(100000L, (a4W_emu - emuWidth) / 2);
                        long startY = (alignment == CardSheetAlignment.TopToBottomPaperSaver)
                            ? 126000L // ~3.5mm
                            : Math.Max(180000L, (a4H_emu - (2 * emuHeight + emuGap)) / 2);

                        foreach (var card in pageCards)
                        {
                            // Front Card (Top)
                            string relIdFront = AddDocxImagePart(mainPart, card.FrontImagePath);
                            var drawFront = CreateAnchorDrawingElement(relIdFront, $"FrontCard_{docPropId}", docPropId++, startX, startY, emuWidth, emuHeight);
                            pageParagraph.Append(new Run(drawFront));

                            // Back Card (Bottom)
                            if (card.HasBack && File.Exists(card.BackImagePath))
                            {
                                long backY = startY + emuHeight + emuGap;
                                string relIdBack = AddDocxImagePart(mainPart, card.BackImagePath);
                                var drawBack = CreateAnchorDrawingElement(relIdBack, $"BackCard_{docPropId}", docPropId++, startX, backY, emuWidth, emuHeight);
                                pageParagraph.Append(new Run(drawBack));
                            }
                        }
                    }
                    else
                    {
                        // 5 Cards / 10 Sides per A4 (Side-by-side Top-to-Bottom Paper Saver)
                        long totalBlockW = (2 * emuWidth) + emuGap;
                        long totalBlockH = (rowCount * emuHeight) + ((rowCount - 1) * emuGap);
                        long startX = Math.Max(100000L, (a4W_emu - totalBlockW) / 2);
                        long startY = (alignment == CardSheetAlignment.TopToBottomPaperSaver)
                            ? 126000L // ~3.5mm top margin
                            : Math.Max(180000L, (a4H_emu - totalBlockH) / 2);

                        for (int r = 0; r < rowCount; r++)
                        {
                            var card = pageCards[r];
                            long curY = startY + r * (emuHeight + emuGap);
                            long frontX = startX;
                            long backX = startX + emuWidth + emuGap;

                            // 1. Left: Front Card
                            string relIdFront = AddDocxImagePart(mainPart, card.FrontImagePath);
                            var drawFront = CreateAnchorDrawingElement(relIdFront, $"FrontCard_{docPropId}", docPropId++, frontX, curY, emuWidth, emuHeight);
                            pageParagraph.Append(new Run(drawFront));

                            // 2. Right: Back Card
                            if (card.HasBack && File.Exists(card.BackImagePath))
                            {
                                string relIdBack = AddDocxImagePart(mainPart, card.BackImagePath);
                                var drawBack = CreateAnchorDrawingElement(relIdBack, $"BackCard_{docPropId}", docPropId++, backX, curY, emuWidth, emuHeight);
                                pageParagraph.Append(new Run(drawBack));
                            }
                        }
                    }

                    body.Append(pageParagraph);

                    // Add Page Break if not last page
                    if (p < totalPages - 1)
                    {
                        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                    }
                }

                body.Append(sectionProps);
                mainPart.Document.Save();
            }

            return outputPath;
        });
    }

    private static string AddDocxImagePart(MainDocumentPart mainPart, string imagePath)
    {
        var imagePart = mainPart.AddImagePart(ImagePartType.Png);
        using var fs = File.OpenRead(imagePath);
        imagePart.FeedData(fs);
        return mainPart.GetIdOfPart(imagePart);
    }

    /// <summary>
    /// Generates a 300 DPI high-resolution A4 image for a specific page (0-indexed).
    /// </summary>
    public async Task<string> GenerateImageAsync(
        IReadOnlyList<ExtractedCardItem> cards,
        string outputPath,
        CardSheetLayout layout = CardSheetLayout.FiveCardsA4PaperSaver,
        double scissorGapMm = 4.0,
        CardSheetAlignment alignment = CardSheetAlignment.TopToBottomPaperSaver,
        int pageIndex = 0,
        double cardWidthCm = CardWidthCm,
        double cardHeightCm = CardHeightCm)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return await Task.Run(() =>
        {
            var validCards = cards.Where(c => c.IsSelected && File.Exists(c.FrontImagePath)).ToList();
            if (validCards.Count == 0)
                throw new InvalidOperationException("No valid card items to generate image.");

            int a4W = 2480;
            int a4H = 3508;
            int cardW = (int)(cardWidthCm * 118.11); // e.g. 945 px for 8.0cm
            int cardH = (int)(cardHeightCm * 118.11); // 638 px for 5.4cm
            int gap = (int)((scissorGapMm / 10.0) * 118.11);

            int cardsPerPage = GetCardsPerPage(layout);
            var pageCards = validCards.Skip(pageIndex * cardsPerPage).Take(cardsPerPage).ToList();
            if (pageCards.Count == 0) pageCards = validCards.Take(cardsPerPage).ToList();

            int rowCount = pageCards.Count;

            using var bmp = new Bitmap(a4W, a4H, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                int totalBlockH = (rowCount * cardH) + ((rowCount - 1) * gap);
                int totalBlockW = (2 * cardW) + gap;
                int startX = Math.Max(30, (a4W - totalBlockW) / 2);

                // Minimal top margin (~3.0mm = 35px at 300 DPI) in TopToBottom Paper Saver mode
                int startY = (alignment == CardSheetAlignment.TopToBottomPaperSaver)
                    ? 35
                    : Math.Max(50, (a4H - totalBlockH) / 2);

                using var dashPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 200, 200), 1.5f)
                {
                    DashStyle = DashStyle.Dash
                };
                using var cutPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(160, 160, 160), 2.0f)
                {
                    DashStyle = DashStyle.DashDot
                };

                for (int r = 0; r < rowCount; r++)
                {
                    var card = pageCards[r];
                    int curY = startY + r * (cardH + gap);
                    int frontX = startX;
                    int backX = startX + cardW + gap;

                    // Draw Front Card
                    if (File.Exists(card.FrontImagePath))
                    {
                        using var msF = new MemoryStream(File.ReadAllBytes(card.FrontImagePath));
                        using var fImg = new Bitmap(msF);
                        g.DrawImage(fImg, frontX, curY, cardW, cardH);
                    }
                    g.DrawRectangle(System.Drawing.Pens.LightGray, frontX, curY, cardW, cardH);

                    // Draw Back Card
                    if (card.HasBack && File.Exists(card.BackImagePath))
                    {
                        using var msB = new MemoryStream(File.ReadAllBytes(card.BackImagePath));
                        using var bImg = new Bitmap(msB);
                        g.DrawImage(bImg, backX, curY, cardW, cardH);
                        g.DrawRectangle(System.Drawing.Pens.LightGray, backX, curY, cardW, cardH);
                    }

                    // Scissor cut line between rows (NO TEXT)
                    if (r < rowCount - 1)
                    {
                        int cutY = curY + cardH + (gap / 2);
                        g.DrawLine(dashPen, 40, cutY, a4W - 40, cutY);
                    }
                }

                // Vertical center cut line (NO TEXT)
                int vertCutX = startX + cardW + (gap / 2);
                g.DrawLine(dashPen, vertCutX, startY - 10, vertCutX, startY + totalBlockH + 10);

                // Scissor reuse cut line (NO TEXT)
                if (alignment == CardSheetAlignment.TopToBottomPaperSaver && rowCount < 5)
                {
                    int finalCutY = startY + totalBlockH + 25;
                    if (finalCutY < a4H - 80)
                    {
                        g.DrawLine(cutPen, 30, finalCutY, a4W - 30, finalCutY);
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            if (ext == ".png")
            {
                bmp.Save(outputPath, ImageFormat.Png);
            }
            else
            {
                var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
                bmp.Save(outputPath, encoder, encoderParams);
            }

            return outputPath;
        });
    }

    private static void DrawImageExact(XGraphics gfx, string imagePath, double x, double y, double width, double height)
    {
        if (!File.Exists(imagePath)) return;
        byte[] imgBytes = File.ReadAllBytes(imagePath);
        using var xImg = XImage.FromStream(() => new MemoryStream(imgBytes));
        gfx.DrawImage(xImg, x, y, width, height);
    }

    private static DocumentFormat.OpenXml.Wordprocessing.Drawing CreateAnchorDrawingElement(
        string relationshipId,
        string name,
        uint id,
        long posX_emus,
        long posY_emus,
        long widthEmus,
        long heightEmus)
    {
        var element = new DocumentFormat.OpenXml.Wordprocessing.Drawing(
            new DW.Anchor(
                new DW.SimplePosition { X = 0L, Y = 0L },
                new DW.HorizontalPosition(
                    new DW.PositionOffset(posX_emus.ToString())
                ) { RelativeFrom = DW.HorizontalRelativePositionValues.Page },
                new DW.VerticalPosition(
                    new DW.PositionOffset(posY_emus.ToString())
                ) { RelativeFrom = DW.VerticalRelativePositionValues.Page },
                new DW.Extent { Cx = widthEmus, Cy = heightEmus },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.WrapSquare { WrapText = DW.WrapTextValues.BothSides },
                new DW.DocProperties { Id = (UInt32Value)id, Name = name },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = (UInt32Value)id, Name = $"{name}.png" },
                                new PIC.NonVisualPictureDrawingProperties()
                            ),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                                new A.Stretch(new A.FillRectangle())
                            ),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = widthEmus, Cy = heightEmus }
                                ),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }
                            )
                        )
                    ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }
                )
            )
            {
                DistanceFromTop = (UInt32Value)0U,
                DistanceFromBottom = (UInt32Value)0U,
                DistanceFromLeft = (UInt32Value)0U,
                DistanceFromRight = (UInt32Value)0U,
                SimplePos = false,
                RelativeHeight = (UInt32Value)(251658240U + id),
                BehindDoc = false,
                Locked = false,
                LayoutInCell = true,
                AllowOverlap = true
            }
        );

        return element;
    }
}
