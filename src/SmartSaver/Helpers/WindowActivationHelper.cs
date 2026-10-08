using System;
using System.Runtime.InteropServices;
using System.Windows;
using Serilog;

namespace SmartSaver.Helpers;

/// <summary>
/// Bulletproof Win32 foreground activation and Z-order management for WPF windows.
/// Ensures dialogs and studio windows pop up directly in front of Windows Explorer and other host windows
/// even when invoked from background services, secondary CLI instances, or IPC triggers.
/// </summary>
public static class WindowActivationHelper
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    public const int ASFW_ANY = -1;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>
    /// Forces a WPF window to restore from minimization, gain top-most Z-order, and receive active input focus.
    /// Uses Win32 AttachThreadInput and SetWindowPos to guarantee the window is placed above Windows Explorer.
    /// </summary>
    /// <param name="window">The target WPF window to bring to the foreground.</param>
    /// <param name="keepTopmost">If true, leaves Topmost=true on the window; if false, temporarily brings it to top then clears Topmost.</param>
    public static void BringToForeground(Window window, bool keepTopmost = true)
    {
        if (window == null) return;

        try
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            // Always assert Topmost first to force DWM to re-stack the window above desktop & explorer
            window.Topmost = true;

            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_SHOW);
                BringWindowToTop(hwnd);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

                IntPtr foregroundHwnd = GetForegroundWindow();
                if (foregroundHwnd != IntPtr.Zero && foregroundHwnd != hwnd)
                {
                    uint foregroundThread = GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
                    uint currentThread = GetCurrentThreadId();

                    if (foregroundThread != currentThread && foregroundThread != 0)
                    {
                        AttachThreadInput(currentThread, foregroundThread, true);
                        SetForegroundWindow(hwnd);
                        BringWindowToTop(hwnd);
                        AttachThreadInput(currentThread, foregroundThread, false);
                    }
                    else
                    {
                        SetForegroundWindow(hwnd);
                    }
                }
                else
                {
                    SetForegroundWindow(hwnd);
                }
            }

            window.Activate();
            window.Focus();

            if (!keepTopmost)
            {
                // Temporarily topmost to snap to front, then release topmost so user can overlap if desired
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        window.Topmost = false;
                        if (hwnd != IntPtr.Zero)
                        {
                            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                        }
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to force foreground activation for window {Title}", window.Title);
        }
    }
}
