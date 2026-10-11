using System.IO;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Cursors = System.Windows.Input.Cursors;

namespace SmartSaver.Services;

/// <summary>
/// Manages the system tray (notification area) icon using Hardcodet.NotifyIcon.Wpf.
/// Provides a context menu with Open Settings, Pause/Resume, View Log, and Exit
/// actions, and exposes corresponding events for the host application to handle.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private MenuItem? _pauseMenuItem;
    private bool _isPaused;
    private bool _disposed;

    /// <summary>Raised when the user selects "Open DASMO Cyber Center" from the tray menu.</summary>
    public event EventHandler? OnOpenDashboard;

    /// <summary>Raised when the user selects "Open Settings" from the tray menu.</summary>
    public event EventHandler? OnOpenSettings;

    /// <summary>Raised when the user selects "A4 Document Stacker" from the tray menu.</summary>
    public event EventHandler? OnOpenStacker;

    /// <summary>Raised when the user selects "Ration / Aadhaar A4 Print" from the tray menu.</summary>
    public event EventHandler? OnOpenGovtCardPrint;

    /// <summary>Raised when the user selects "Merge PDF Files" from the tray menu.</summary>
    public event EventHandler? OnOpenMerge;

    /// <summary>Raised when the user selects "Extract / Split PDF" from the tray menu.</summary>
    public event EventHandler? OnOpenSplit;

    /// <summary>Raised when the user selects "Convert Images to PDF" from the tray menu.</summary>
    public event EventHandler? OnOpenImg2Pdf;

    /// <summary>Raised when the user selects "Convert PDF to Images" from the tray menu.</summary>
    public event EventHandler? OnOpenPdf2Img;

    /// <summary>Raised when the user selects "Enhance Phone Scan" from the tray menu.</summary>
    public event EventHandler? OnOpenEnhance;

    /// <summary>Raised when the user selects "Add Name & Date Stamp" from the tray menu.</summary>
    public event EventHandler? OnOpenStamp;

    /// <summary>Raised when the user selects "Resize Signature" from the tray menu.</summary>
    public event EventHandler? OnOpenSignatureResize;

    /// <summary>Raised when the user selects "PDF Editor Studio" from the tray menu.</summary>
    public event EventHandler? OnOpenPdfEditor;

    /// <summary>Raised when the user selects "Today's Output History" from the tray menu.</summary>
    public event EventHandler? OnOpenHistory;

    /// <summary>Raised when the user selects "Print Counter & Billing Studio" from the tray menu.</summary>
    public event EventHandler? OnOpenPrintCounter;

    /// <summary>Raised when the user toggles the Pause/Resume state.</summary>
    public event EventHandler<bool>? OnPauseToggle;

    /// <summary>Raised when the user selects "View Log" from the tray menu.</summary>
    public event EventHandler? OnViewLog;

    /// <summary>Raised when the user selects "Exit" from the tray menu.</summary>
    public event EventHandler? OnExit;

    /// <summary>Gets whether the service is currently in paused state.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Initializes and displays the system tray icon with context menu.
    /// Must be called from the UI (STA) thread.
    /// </summary>
    public void Initialize()
    {
        if (_taskbarIcon is not null)
        {
            Log.Warning("TrayIconService is already initialized");
            return;
        }

        try
        {
            _taskbarIcon = new TaskbarIcon
            {
                ToolTipText = "DASMO CYBER CAFE TOOLS",
                ContextMenu = BuildContextMenu(),
                Visibility = Visibility.Visible
            };

            // Use embedded application icon or a default
            SetIcon(paused: false);

            _taskbarIcon.TrayMouseDoubleClick += (_, _) => OnOpenDashboard?.Invoke(this, EventArgs.Empty);

            Log.Information("System tray icon initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize system tray icon");
        }
    }

    /// <summary>
    /// Toggles the paused state and updates the tray icon appearance.
    /// </summary>
    public void TogglePause()
    {
        _isPaused = !_isPaused;
        UpdatePauseState();
        OnPauseToggle?.Invoke(this, _isPaused);
    }

    /// <summary>
    /// Sets the paused state explicitly and updates the tray icon.
    /// </summary>
    /// <param name="paused">Whether the service should be paused.</param>
    public void SetPaused(bool paused)
    {
        if (_isPaused == paused) return;

        _isPaused = paused;
        UpdatePauseState();
        OnPauseToggle?.Invoke(this, _isPaused);
    }

    public void SetVisible(bool visible)
    {
        if (_taskbarIcon != null)
        {
            _taskbarIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Shows a balloon tip notification from the tray icon.
    /// </summary>
    /// <param name="title">Balloon title.</param>
    /// <param name="message">Balloon message body.</param>
    /// <param name="icon">Balloon icon type.</param>
    public void ShowBalloon(string title, string message, BalloonIcon icon = BalloonIcon.Info)
    {
        try
        {
            _taskbarIcon?.ShowBalloonTip(title, message, icon);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show balloon tip");
        }
    }

    private static readonly ControlTemplate TrayContextMenuTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                 TargetType=""{x:Type ContextMenu}"">
    <Border Background=""#0F172A""
            BorderBrush=""#253248""
            BorderThickness=""1""
            CornerRadius=""12""
            Padding=""4,6,4,6""
            SnapsToDevicePixels=""True""
            MinWidth=""315"">
        <Border.Effect>
            <DropShadowEffect BlurRadius=""24"" ShadowDepth=""6"" Direction=""270"" Color=""#000000"" Opacity=""0.65"" />
        </Border.Effect>
        <ScrollViewer CanContentScroll=""True""
                      VerticalScrollBarVisibility=""Auto""
                      HorizontalScrollBarVisibility=""Disabled"">
            <StackPanel IsItemsHost=""True"" KeyboardNavigation.DirectionalNavigation=""Cycle"" />
        </ScrollViewer>
    </Border>
</ControlTemplate>");

    private static readonly ControlTemplate TrayMenuItemTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                 TargetType=""{x:Type MenuItem}"">
    <Border x:Name=""ItemBorder""
            Background=""Transparent""
            BorderBrush=""Transparent""
            BorderThickness=""1""
            CornerRadius=""6""
            Margin=""3,1,3,1""
            Padding=""{TemplateBinding Padding}""
            Cursor=""Hand""
            SnapsToDevicePixels=""True"">
        <ContentPresenter ContentSource=""Header""
                          RecognizesAccessKey=""True""
                          VerticalAlignment=""Center"" />
    </Border>
    <ControlTemplate.Triggers>
        <Trigger Property=""IsHighlighted"" Value=""True"">
            <Setter TargetName=""ItemBorder"" Property=""Background"" Value=""#1E293B"" />
            <Setter TargetName=""ItemBorder"" Property=""BorderBrush"" Value=""#38BDF8"" />
            <Setter Property=""Foreground"" Value=""#38BDF8"" />
        </Trigger>
        <Trigger Property=""IsEnabled"" Value=""False"">
            <Setter Property=""Opacity"" Value=""0.4"" />
        </Trigger>
    </ControlTemplate.Triggers>
</ControlTemplate>");

    private static readonly ControlTemplate TrayExitMenuItemTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                 TargetType=""{x:Type MenuItem}"">
    <Border x:Name=""ItemBorder""
            Background=""Transparent""
            BorderBrush=""Transparent""
            BorderThickness=""1""
            CornerRadius=""6""
            Margin=""3,1,3,1""
            Padding=""{TemplateBinding Padding}""
            Cursor=""Hand""
            SnapsToDevicePixels=""True"">
        <ContentPresenter ContentSource=""Header""
                          RecognizesAccessKey=""True""
                          VerticalAlignment=""Center"" />
    </Border>
    <ControlTemplate.Triggers>
        <Trigger Property=""IsHighlighted"" Value=""True"">
            <Setter TargetName=""ItemBorder"" Property=""Background"" Value=""#3F1717"" />
            <Setter TargetName=""ItemBorder"" Property=""BorderBrush"" Value=""#EF4444"" />
            <Setter Property=""Foreground"" Value=""#FCA5A5"" />
        </Trigger>
        <Trigger Property=""IsEnabled"" Value=""False"">
            <Setter Property=""Opacity"" Value=""0.4"" />
        </Trigger>
    </ControlTemplate.Triggers>
</ControlTemplate>");

    private static readonly ControlTemplate TraySeparatorTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                 TargetType=""{x:Type Separator}"">
    <Border Background=""#1E293B"" Height=""1"" Margin=""8,4,8,4"" SnapsToDevicePixels=""True"" />
</ControlTemplate>");

    private Border? _headerStatusPill;
    private TextBlock? _headerStatusText;
    private TextBlock? _pauseIconTb;
    private TextBlock? _pauseTitleTb;

    internal ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Template = TrayContextMenuTemplate,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };

        MenuItem MakeItem(string icon, string title, RoutedEventHandler onClick, bool isExit = false)
        {
            var grid = new Grid { SnapsToDevicePixels = true };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconTb = new TextBlock
            {
                Text = icon,
                FontSize = 13.5,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(iconTb, 0);

            var titleTb = new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 8, 0)
            };
            Grid.SetColumn(titleTb, 1);

            grid.Children.Add(iconTb);
            grid.Children.Add(titleTb);

            var item = new MenuItem
            {
                Header = grid,
                Template = isExit ? TrayExitMenuItemTemplate : TrayMenuItemTemplate,
                Foreground = isExit
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113)) // #F87171 soft red
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)), // #F1F5F9 crisp white
                Padding = new Thickness(8, 6, 10, 6),
                Cursor = Cursors.Hand
            };

            item.Click += onClick;
            return item;
        }

        UIElement MakeSectionHeader(string title)
        {
            var border = new Border
            {
                Padding = new Thickness(10, 8, 10, 3),
                Margin = new Thickness(0, 2, 0, 1),
                SnapsToDevicePixels = true
            };
            var tb = new TextBlock
            {
                Text = title,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)), // #64748B
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            border.Child = tb;
            return border;
        }

        Separator MakeSep()
        {
            return new Separator
            {
                Template = TraySeparatorTemplate
            };
        }

        // --- 1. Top Header Card (Clickable to Open Dashboard) ---
        var headerCard = new Border
        {
            Background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(21, 27, 40),
                System.Windows.Media.Color.FromRgb(15, 23, 42),
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 1)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 43, 61)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(3, 2, 3, 4),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBadge = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(59, 130, 246),
                System.Windows.Media.Color.FromRgb(14, 165, 233),
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 1)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconTxt = new TextBlock
        {
            Text = "⚡",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBadge.Child = iconTxt;
        Grid.SetColumn(iconBadge, 0);

        var titleSp = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        var titleTb = new TextBlock
        {
            Text = "DASMO CYBER CAFE TOOLS",
            FontWeight = FontWeights.Bold,
            FontSize = 11.5,
            Foreground = System.Windows.Media.Brushes.White
        };
        string appVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.5.54";
        var subtitleTb = new TextBlock
        {
            Text = $"Daily Cyber Cafe Suite • v{appVer}",
            FontSize = 9.5,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184))
        };
        titleSp.Children.Add(titleTb);
        titleSp.Children.Add(subtitleTb);
        Grid.SetColumn(titleSp, 1);

        _headerStatusPill = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(6, 78, 59)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105)),
            BorderThickness = new Thickness(1)
        };
        _headerStatusText = new TextBlock
        {
            Text = "● ACTIVE",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153))
        };
        _headerStatusPill.Child = _headerStatusText;
        Grid.SetColumn(_headerStatusPill, 2);

        headerGrid.Children.Add(iconBadge);
        headerGrid.Children.Add(titleSp);
        headerGrid.Children.Add(_headerStatusPill);
        headerCard.Child = headerGrid;

        headerCard.MouseEnter += (_, _) =>
        {
            headerCard.Background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(30, 41, 59),
                System.Windows.Media.Color.FromRgb(21, 27, 40),
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 1));
            headerCard.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
        };
        headerCard.MouseLeave += (_, _) =>
        {
            headerCard.Background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(21, 27, 40),
                System.Windows.Media.Color.FromRgb(15, 23, 42),
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 1));
            headerCard.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 43, 61));
        };
        headerCard.MouseLeftButtonUp += (_, _) =>
        {
            menu.IsOpen = false;
            Log.Information("Tray menu: Clicked header banner -> Open Dashboard");
            OnOpenDashboard?.Invoke(this, EventArgs.Empty);
        };
        menu.Items.Add(headerCard);

        // --- 2. Open All Tools Dashboard Action ---
        var openDashboardItem = MakeItem("⚡", "Open All Tools Dashboard", (_, _) =>
        {
            Log.Information("Tray menu: Open Dashboard");
            OnOpenDashboard?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openDashboardItem);

        menu.Items.Add(MakeSep());

        // --- 3. DAILY CAFE STUDIOS & COUNTER ---
        menu.Items.Add(MakeSectionHeader("DAILY CAFE STUDIOS & COUNTER"));

        menu.Items.Add(MakeItem("🖨️", "Print Counter & Billing Studio...", (_, _) =>
        {
            Log.Information("Tray menu: Open Print Counter & Billing Studio");
            OnOpenPrintCounter?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("📑", "A4 Document Stacker", (_, _) =>
        {
            Log.Information("Tray menu: Open Stacker");
            OnOpenStacker?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("💳", "Ration / Aadhaar A4 Print...", (_, _) =>
        {
            Log.Information("Tray menu: Open Ration / Aadhaar A4 Print");
            OnOpenGovtCardPrint?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("📝", "PDF Editor Studio (In-Place Edit)...", (_, _) =>
        {
            Log.Information("Tray menu: Open PDF Editor Studio");
            OnOpenPdfEditor?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeSep());

        // --- 4. DOCUMENT & CONVERSION UTILITIES ---
        menu.Items.Add(MakeSectionHeader("DOCUMENT & CONVERSION UTILITIES"));

        menu.Items.Add(MakeItem("✨", "Enhance Phone Scan (Magic White)...", (_, _) =>
        {
            Log.Information("Tray menu: Open Scan Enhancer");
            OnOpenEnhance?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("✍️", "Resize Signature & Photo (Exact px/cm)...", (_, _) =>
        {
            Log.Information("Tray menu: Open Resize Signature");
            OnOpenSignatureResize?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("🏷️", "Add Name & Date Stamp (SSC Photo)...", (_, _) =>
        {
            Log.Information("Tray menu: Open Photo Stamp");
            OnOpenStamp?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("📂", "Merge PDF Files...", (_, _) =>
        {
            Log.Information("Tray menu: Open Merge PDFs");
            OnOpenMerge?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("✂️", "Split / Extract PDF...", (_, _) =>
        {
            Log.Information("Tray menu: Open Split PDF");
            OnOpenSplit?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("🖼️", "Convert Images to PDF (A4)...", (_, _) =>
        {
            Log.Information("Tray menu: Open Images to PDF");
            OnOpenImg2Pdf?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("📄", "Convert PDF to JPG Images...", (_, _) =>
        {
            Log.Information("Tray menu: Open PDF to Images");
            OnOpenPdf2Img?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeSep());

        // --- 5. SYSTEM & PREFERENCES ---
        menu.Items.Add(MakeSectionHeader("SYSTEM & PREFERENCES"));

        menu.Items.Add(MakeItem("🕒", "Today's Output History Hub...", (_, _) =>
        {
            Log.Information("Tray menu: Open Output History");
            OnOpenHistory?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeItem("⚙️", "Open Settings & Folders", (_, _) =>
        {
            Log.Information("Tray menu: Open Settings");
            OnOpenSettings?.Invoke(this, EventArgs.Empty);
        }));

        // Pause / Resume Item with Dynamic Typography
        var pauseGrid = new Grid { SnapsToDevicePixels = true };
        pauseGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        pauseGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _pauseIconTb = new TextBlock
        {
            Text = _isPaused ? "▶️" : "⏸️",
            FontSize = 13.5,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji, Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_pauseIconTb, 0);

        _pauseTitleTb = new TextBlock
        {
            Text = _isPaused ? "Resume Auto-Compression" : "Pause Auto-Compression",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 8, 0)
        };
        Grid.SetColumn(_pauseTitleTb, 1);

        pauseGrid.Children.Add(_pauseIconTb);
        pauseGrid.Children.Add(_pauseTitleTb);

        _pauseMenuItem = new MenuItem
        {
            Header = pauseGrid,
            Template = TrayMenuItemTemplate,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)),
            Padding = new Thickness(8, 6, 10, 6),
            Cursor = Cursors.Hand
        };
        _pauseMenuItem.Click += (_, _) =>
        {
            TogglePause();
            Log.Debug("Tray menu: {Action}", _isPaused ? "Paused" : "Resumed");
        };
        menu.Items.Add(_pauseMenuItem);

        menu.Items.Add(MakeItem("📋", "View Activity Log", (_, _) =>
        {
            Log.Debug("Tray menu: View Log");
            OnViewLog?.Invoke(this, EventArgs.Empty);
        }));

        menu.Items.Add(MakeSep());

        // --- 6. Exit Action (Red Alert Hover) ---
        menu.Items.Add(MakeItem("🚪", "Exit DASMO Tools", (_, _) =>
        {
            Log.Debug("Tray menu: Exit");
            OnExit?.Invoke(this, EventArgs.Empty);
        }, isExit: true));

        return menu;
    }

    private void UpdatePauseState()
    {
        if (_pauseIconTb != null && _pauseTitleTb != null)
        {
            _pauseIconTb.Text = _isPaused ? "▶️" : "⏸️";
            _pauseTitleTb.Text = _isPaused ? "Resume Auto-Compression" : "Pause Auto-Compression";
        }

        if (_headerStatusText != null && _headerStatusPill != null)
        {
            if (_isPaused)
            {
                _headerStatusText.Text = "⏸ PAUSED";
                _headerStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36));
                _headerStatusPill.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 26, 3));
                _headerStatusPill.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 119, 6));
            }
            else
            {
                _headerStatusText.Text = "● ACTIVE";
                _headerStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153));
                _headerStatusPill.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(6, 78, 59));
                _headerStatusPill.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105));
            }
        }

        if (_taskbarIcon is not null)
        {
            _taskbarIcon.ToolTipText = _isPaused
                ? "DASMO CYBER CAFE TOOLS — PAUSED"
                : "DASMO CYBER CAFE TOOLS — Active Cyber Suite & Auto-Compressor";
        }

        SetIcon(_isPaused);
        Log.Information("DASMO CYBER CAFE TOOLS is now {State}", _isPaused ? "paused" : "active");
    }

    private System.Drawing.Icon? _currentTrayIcon;

    private void SetIcon(bool paused)
    {
        if (_taskbarIcon == null) return;

        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "tray_icon.ico");
            System.Drawing.Icon? loadedIcon = null;

            if (File.Exists(iconPath))
            {
                loadedIcon = new System.Drawing.Icon(iconPath, 32, 32);
            }
            else
            {
                var uri = new Uri("pack://application:,,,/Resources/tray_icon.ico", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(uri);
                if (streamInfo != null)
                {
                    using var stream = streamInfo.Stream;
                    loadedIcon = new System.Drawing.Icon(stream, 32, 32);
                }
            }

            if (loadedIcon != null)
            {
                var oldIcon = _currentTrayIcon;
                _currentTrayIcon = loadedIcon;
                _taskbarIcon.Icon = loadedIcon;
                oldIcon?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to set custom tray icon");
        }
    }

    /// <summary>Removes the tray icon and releases resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_taskbarIcon is not null)
            {
                _taskbarIcon.Visibility = Visibility.Collapsed;
                _taskbarIcon.Dispose();
                _taskbarIcon = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing tray icon");
        }

        GC.SuppressFinalize(this);
    }
}
