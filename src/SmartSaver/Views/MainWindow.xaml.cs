using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmartSaver.Models;
using SmartSaver.ViewModels;

namespace SmartSaver.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel? viewModel = null)
    {
        InitializeComponent();
        ViewModel = viewModel ?? new MainViewModel();
        DataContext = ViewModel;

        ViewModel.RequestClose = () =>
        {
            this.Hide();
        };

        ViewModel.RequestOpenStacker = () =>
        {
            App.CurrentApp?.ShowStackerWindow();
        };

        ViewModel.RequestOpenCompressDialog = () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select File to Compress",
                Filter = "All Supported Files (*.pdf;*.jpg;*.jpeg;*.png;*.webp;*.docx;*.xlsx;*.pptx)|*.pdf;*.jpg;*.jpeg;*.png;*.webp;*.docx;*.xlsx;*.pptx|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                App.CurrentApp?.ShowCompressDialog(dlg.FileName);
            }
        };

        Loaded += (s, e) => UpdateAdminVisibility();
    }

    private void UpdateAdminVisibility()
    {
        bool isSuperAdmin = SmartSaver.Services.FirebaseCloudAuthService.Instance.IsSuperAdmin;
        if (BtnTopAdminPanel != null)
            BtnTopAdminPanel.Visibility = isSuperAdmin ? Visibility.Visible : Visibility.Collapsed;
        if (BtnSidebarAdminPanel != null)
            BtnSidebarAdminPanel.Visibility = isSuperAdmin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AdminPanel_Click(object sender, RoutedEventArgs e)
    {
        if (SmartSaver.Services.FirebaseCloudAuthService.Instance.IsSuperAdmin)
        {
            var adminWin = new AdminPanelWindow { Owner = this };
            adminWin.ShowDialog();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Hide to background tray instead of shutting down the whole app
        this.Hide();
    }

    private void GovtCardTab_DragOver(object sender, System.Windows.DragEventArgs e)
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

    private void GovtCardTab_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                _ = ViewModel.GovtCardVm.AddSourceFilesAsync(files);
            }
        }
    }

    private void SignatureTab_DragOver(object sender, System.Windows.DragEventArgs e)
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

    private void SignatureTab_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && System.IO.File.Exists(files[0]))
            {
                ViewModel.SignatureResizeVm.LoadFile(files[0]);
            }
        }
    }

    #region Split PDF Page Organizer Drag & Drop

    private System.Windows.Point _splitPageDragStart;

    private void SplitPageCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _splitPageDragStart = e.GetPosition(null);
    }

    private void SplitPageCard_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (e.OriginalSource is DependencyObject dep && FindVisualAncestor<System.Windows.Controls.Primitives.ButtonBase>(dep) != null)
                return;

            System.Windows.Point current = e.GetPosition(null);
            Vector diff = _splitPageDragStart - current;
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
                    ViewModel.SplitPdfVm.ReorderPage(sourceItem, targetItem);
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
                ViewModel.SplitPdfVm.RemovePage(droppedItem);
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
                        if (!ViewModel.SplitPdfVm.HasPages)
                        {
                            ViewModel.SplitPdfVm.LoadFile(file);
                        }
                        else
                        {
                            ViewModel.SplitPdfVm.AppendPdfFile(file);
                        }
                    }
                }
            }
        }
    }

    #endregion
}
