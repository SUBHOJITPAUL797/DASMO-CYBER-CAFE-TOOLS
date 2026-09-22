using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace SmartSaver.Services;

public class ExplorerKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_SPACE = 0x20;

    private static HookProc? _staticProc;
    private static IntPtr _hookID = IntPtr.Zero;
    private static DateTime _lastTriggerTime = DateTime.MinValue;
    private static Action<string>? _staticPeekHandler;

    public event Action<string>? OnSpacebarPeekTriggered
    {
        add => _staticPeekHandler += value;
        remove => _staticPeekHandler -= value;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    public ExplorerKeyboardHook()
    {
        Start();
    }

    public void Start()
    {
        if (_hookID != IntPtr.Zero) return;

        // Keep static reference to delegate so GC never collects it
        _staticProc = HookCallback;
        _hookID = SetHook(_staticProc);
        Log.Information("Explorer Spacebar Quick Peek keyboard hook started successfully");
    }

    public void Stop()
    {
        if (_hookID != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookID);
            _hookID = IntPtr.Zero;
            Log.Information("Explorer Spacebar Quick Peek keyboard hook stopped");
        }
    }

    private IntPtr SetHook(HookProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule?.ModuleName), 0);
    }

    // -----------------------------------------------------------------------
    // Win32 P/Invoke declarations
    // -----------------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;    // The window with keyboard focus
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;   // Non-null when a text caret exists (user is typing)
        public RECT rcCaret;
    }

    /// <summary>
    /// Returns the class name of the given window handle.
    /// </summary>
    private static string GetWindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Returns true if Windows Explorer (or Desktop) is the foreground window,
    /// AND the user is NOT currently renaming a file (no focused Edit control / no active caret).
    /// </summary>
    private static bool IsExplorerForegroundAndNotRenaming()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        string topClass = GetWindowClass(foreground);

        bool isExplorer = topClass.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
                          topClass.Equals("ExplorerWClass", StringComparison.OrdinalIgnoreCase) ||
                          topClass.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
                          topClass.Equals("Progman", StringComparison.OrdinalIgnoreCase);

        if (!isExplorer) return false;

        // --- Rename detection ---
        // Get the thread that owns the foreground window
        uint threadId = GetWindowThreadProcessId(foreground, out _);

        var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(threadId, ref gui))
        {
            // 1. If a caret exists, the user is actively typing in a text field (rename / search bar)
            if (gui.hwndCaret != IntPtr.Zero)
            {
                Log.Debug("Spacebar suppressed — caret detected in Explorer (user is typing/renaming)");
                return false;
            }

            // 2. Check the focused child control class — rename edit boxes are class "Edit",
            //    "LiteralEditHost" (Windows 11 inline rename), or "NetUIHWND" (search bar)
            if (gui.hwndFocus != IntPtr.Zero && gui.hwndFocus != foreground)
            {
                string focusedClass = GetWindowClass(gui.hwndFocus);
                if (focusedClass.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                    focusedClass.Contains("LiteralEditHost", StringComparison.OrdinalIgnoreCase) ||
                    focusedClass.Contains("NetUI", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("Spacebar suppressed — focused control is '{Class}' (rename/search mode)", focusedClass);
                    return false;
                }
            }
        }

        return true;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_SPACE && IsExplorerForegroundAndNotRenaming())
            {
                if ((DateTime.Now - _lastTriggerTime).TotalMilliseconds > 300)
                {
                    _lastTriggerTime = DateTime.Now;

                    // Execute Explorer selection lookup asynchronously on thread pool so low-level hook never times out
                    Task.Run(() =>
                    {
                        try
                        {
                            string? selectedPath = ExplorerSelectionHelper.GetSelectedFilePathInExplorer();
                            if (!string.IsNullOrEmpty(selectedPath))
                            {
                                Log.Information("Spacebar Quick Peek triggered for file: {Path}", selectedPath);

                                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                                {
                                    _staticPeekHandler?.Invoke(selectedPath);
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Error in Spacebar Quick Peek hook callback");
                        }
                    });
                }
            }
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
