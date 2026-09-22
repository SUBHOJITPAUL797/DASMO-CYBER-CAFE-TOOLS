using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmartSaver.Models;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class SplitPdfDialog : Window
{
    private readonly SplitPdfViewModel _vm;

    public SplitPdfDialog(string filePath)
    {
        InitializeComponent();
        _vm = new SplitPdfViewModel(filePath);
        _vm.RequestClose = () => Close();
        DataContext = _vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    #region Drag & Drop Event Handlers

    private System.Windows.Point _pageDragStart;

    private void SplitPageCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pageDragStart = e.GetPosition(null);
    }

    private void SplitPageCard_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (e.OriginalSource is DependencyObject dep && FindVisualAncestor<System.Windows.Controls.Primitives.ButtonBase>(dep) != null)
                return;

            System.Windows.Point current = e.GetPosition(null);
            Vector diff = _pageDragStart - current;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is FrameworkElement elem && elem.DataContext is PdfPageItem item)
                {
                    DragDrop.DoDragDrop(elem, item, System.Windows.DragDropEffects.Move);
                }
            }
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            else
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }
        return null;
    }

    private void SplitPageCard_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(PdfPageItem)))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is PdfPageItem targetItem)
            {
                targetItem.IsDraggingOver = true;
            }
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
    }

    private void SplitPageCard_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is PdfPageItem targetItem)
        {
            targetItem.IsDraggingOver = false;
        }
    }

    private void SplitPageCard_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is PdfPageItem targetItem)
        {
            targetItem.IsDraggingOver = false;
            if (e.Data.GetDataPresent(typeof(PdfPageItem)))
            {
                var sourceItem = e.Data.GetData(typeof(PdfPageItem)) as PdfPageItem;
                if (sourceItem != null && targetItem != null && sourceItem != targetItem)
                {
                    _vm.ReorderPage(sourceItem, targetItem);
                }
            }
        }
    }

    private void SplitDeleteZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(PdfPageItem)))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            if (sender is Border b)
            {
                b.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x52, 0x1B, 0x27));
                b.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x47, 0x57));
            }
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
    }

    private void SplitDeleteZone_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is Border b)
        {
            b.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x26, 0x18, 0x22));
            b.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x77, 0x20, 0x33));
        }
    }

    private void SplitDeleteZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is Border b)
        {
            b.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x26, 0x18, 0x22));
            b.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x77, 0x20, 0x33));
        }

        if (e.Data.GetDataPresent(typeof(PdfPageItem)))
        {
            var droppedItem = e.Data.GetData(typeof(PdfPageItem)) as PdfPageItem;
            if (droppedItem != null)
            {
                _vm.RemovePage(droppedItem);
            }
        }
    }

    private void SplitPageContainer_DragOver(object sender, System.Windows.DragEventArgs e)
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

    private void SplitPageContainer_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                foreach (var file in files)
                {
                    if (string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_vm.HasPages)
                        {
                            _vm.LoadFile(file);
                        }
                        else
                        {
                            _vm.AppendPdfFile(file);
                        }
                    }
                }
            }
        }
    }

    #endregion
}
