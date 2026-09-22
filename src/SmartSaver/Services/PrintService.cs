using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using SmartSaver.Views;
using WpfMessageBox = System.Windows.MessageBox;
using WpfSize = System.Windows.Size;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfImage = System.Windows.Controls.Image;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;
using WpfApplication = System.Windows.Application;

namespace SmartSaver.Services;

public static class PrintService
{
    /// <summary>
    /// Renders any WPF Visual into a 300 DPI high-resolution RenderTargetBitmap for printing.
    /// </summary>
    public static BitmapSource RenderVisualToBitmap(Visual visual, double widthDIP, double heightDIP, int dpi = 300)
    {
        int pixelWidth = (int)Math.Ceiling(widthDIP * (dpi / 96.0));
        int pixelHeight = (int)Math.Ceiling(heightDIP * (dpi / 96.0));

        var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Prompts the user with the custom Adobe-style Native Print Dialog and prints a WPF Visual directly.
    /// </summary>
    public static bool PrintVisual(Visual visual, string documentTitle)
    {
        try
        {
            var bounds = VisualTreeHelper.GetDescendantBounds(visual);
            double width = Math.Max(200, bounds.Width);
            double height = Math.Max(200, bounds.Height);

            var bmp = RenderVisualToBitmap(visual, width, height);

            if (WpfApplication.Current?.Dispatcher != null && !WpfApplication.Current.Dispatcher.CheckAccess())
            {
                return WpfApplication.Current.Dispatcher.Invoke(() => NativePrintDialog.ShowPrintDialog(bmp, documentTitle));
            }
            return NativePrintDialog.ShowPrintDialog(bmp, documentTitle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to print visual: {Title}", documentTitle);
            WpfMessageBox.Show($"Failed to print document:\n{ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Prompts the user with the custom Adobe-style Native Print Dialog and prints an image directly.
    /// </summary>
    public static bool PrintImageDirect(ImageSource imageSource, string title, double targetWidthCm = 0, double targetHeightCm = 0)
    {
        try
        {
            if (WpfApplication.Current?.Dispatcher != null && !WpfApplication.Current.Dispatcher.CheckAccess())
            {
                return WpfApplication.Current.Dispatcher.Invoke(() => NativePrintDialog.ShowPrintDialog(imageSource, title));
            }
            return NativePrintDialog.ShowPrintDialog(imageSource, title);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Direct image print failed for {Title}", title);
            WpfMessageBox.Show($"Printing failed:\n{ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Generates a Passport Photo Studio Print Grid on 4x6 (6 or 8 copies) or A4 (30+ copies) and displays the Native Print Dialog.
    /// </summary>
    public static bool PrintPassportPhotoSheet(ImageSource photoSource, int copies, string paperSize = "4x6", string candidateName = "")
    {
        try
        {
            // Standard A4 or 4x6 dimensions in 96 DIP
            bool is4x6 = paperSize.Equals("4x6", StringComparison.OrdinalIgnoreCase);
            double pageWidth = is4x6 ? 4.0 * 96.0 : 8.27 * 96.0;   // 384 DIP or ~793.9 DIP
            double pageHeight = is4x6 ? 6.0 * 96.0 : 11.69 * 96.0; // 576 DIP or ~1122.2 DIP

            var pageGrid = new Grid
            {
                Width = pageWidth,
                Height = pageHeight,
                Background = WpfBrushes.White,
                Margin = new Thickness(12)
            };

            // Standard Passport dimensions: 3.5cm x 4.5cm -> ~132.3 x 170.1 DIP
            double photoWidthDIP = 3.5 * (96.0 / 2.54);
            double photoHeightDIP = 4.5 * (96.0 / 2.54);

            var wrapPanel = new WrapPanel
            {
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = WpfVerticalAlignment.Center,
                Margin = new Thickness(8)
            };

            for (int i = 0; i < copies; i++)
            {
                var border = new Border
                {
                    Width = photoWidthDIP,
                    Height = photoHeightDIP,
                    BorderBrush = new SolidColorBrush(WpfColor.FromRgb(200, 200, 200)),
                    BorderThickness = new Thickness(0.5),
                    Margin = new Thickness(3),
                    Background = WpfBrushes.White
                };

                var img = new WpfImage
                {
                    Source = photoSource,
                    Stretch = Stretch.UniformToFill
                };

                border.Child = img;
                wrapPanel.Children.Add(border);
            }

            pageGrid.Children.Add(wrapPanel);

            // Measure and arrange for rendering
            pageGrid.Measure(new WpfSize(pageWidth, pageHeight));
            pageGrid.Arrange(new WpfRect(new WpfPoint(0, 0), new WpfSize(pageWidth, pageHeight)));
            pageGrid.UpdateLayout();

            string jobTitle = string.IsNullOrWhiteSpace(candidateName)
                ? $"Passport_Photos_{copies}_Copies"
                : $"Passport_{candidateName}_{copies}_Copies";

            var sheetBmp = RenderVisualToBitmap(pageGrid, pageWidth, pageHeight, 300);

            if (WpfApplication.Current?.Dispatcher != null && !WpfApplication.Current.Dispatcher.CheckAccess())
            {
                return WpfApplication.Current.Dispatcher.Invoke(() => NativePrintDialog.ShowPrintDialog(sheetBmp, jobTitle));
            }
            return NativePrintDialog.ShowPrintDialog(sheetBmp, jobTitle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to print passport photo sheet");
            WpfMessageBox.Show($"Failed to print photo sheet:\n{ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Displays the custom Adobe-style Native Print Dialog for multi-page document images (e.g. 5 Cards/A4 sheets).
    /// </summary>
    public static bool PrintMultipleImagesDirect(IReadOnlyList<string> imagePaths, string title)
    {
        if (imagePaths == null || imagePaths.Count == 0) return false;

        try
        {
            if (WpfApplication.Current?.Dispatcher != null && !WpfApplication.Current.Dispatcher.CheckAccess())
            {
                return WpfApplication.Current.Dispatcher.Invoke(() => NativePrintDialog.ShowPrintDialog(imagePaths, title));
            }
            return NativePrintDialog.ShowPrintDialog(imagePaths, title);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show native print dialog for multi-page document: {Title}", title);
            WpfMessageBox.Show($"Printing failed:\n{ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
