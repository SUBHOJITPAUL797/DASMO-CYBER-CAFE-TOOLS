using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

#pragma warning disable CS8602

namespace SmartSaver.Services;

public static class ExplorerSelectionHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public static string? GetSelectedFilePathInExplorer()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            // Check focused class name
            string focusedClass = GetWindowClassName(hwnd);

            // Skip spacebar trigger if user is typing in a text box (e.g. rename or search input)
            if (focusedClass.Contains("Edit", StringComparison.OrdinalIgnoreCase))
                return null;

            IntPtr topLevelHwnd = GetAncestor(hwnd, GA_ROOT);
            if (topLevelHwnd == IntPtr.Zero) topLevelHwnd = hwnd;

            string topClass = GetWindowClassName(topLevelHwnd);

            // CabinetWClass = Windows Explorer, Progman/WorkerW = Desktop
            bool isExplorer = topClass == "CabinetWClass" || topClass == "ExplorerWClass" ||
                              focusedClass == "CabinetWClass" || focusedClass == "DirectUIHWND" ||
                              focusedClass == "SysListView32" || focusedClass == "ShellTabWindowClass";

            bool isDesktop = topClass == "Progman" || topClass == "WorkerW" || focusedClass == "Progman" || focusedClass == "WorkerW";

            if (!isExplorer && !isDesktop)
                return null;

            string topTitle = GetWindowTitle(topLevelHwnd);

            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;

            dynamic windows = shell.Windows();
            int count = windows.Count;

            // 1. Primary Method: Match active tab LocationName against Windows 11 Explorer top window title
            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window == null) continue;

                try
                {
                    long winHwnd = (long)window.HWND;
                    if (winHwnd == (long)topLevelHwnd || winHwnd == (long)hwnd)
                    {
                        string locName = window.LocationName ?? string.Empty;
                        if (!string.IsNullOrEmpty(locName) &&
                            (topTitle.StartsWith(locName, StringComparison.OrdinalIgnoreCase) ||
                             topTitle.Contains(locName, StringComparison.OrdinalIgnoreCase)))
                        {
                            dynamic? doc = window.Document;
                            if (doc != null)
                            {
                                dynamic? selectedItems = doc.SelectedItems();
                                if (selectedItems != null && selectedItems.Count > 0)
                                {
                                    dynamic? firstItem = selectedItems.Item(0);
                                    if (firstItem != null)
                                    {
                                        string? path = firstItem.Path;
                                        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                                        {
                                            Log.Information("Active tab selection retrieved for '{Location}': {Path}", locName, path);
                                            return path;
                                        }
                                    }
                                }

                                dynamic? focusedItem = doc.FocusedItem;
                                if (focusedItem != null)
                                {
                                    string? path = focusedItem.Path;
                                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                                    {
                                        Log.Information("Active tab focused item retrieved for '{Location}': {Path}", locName, path);
                                        return path;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Exception querying tab for active title match");
                }
            }

            // 2. Fallback Method: Query SelectedItems on any matching window HWND
            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window == null) continue;

                try
                {
                    long winHwnd = (long)window.HWND;
                    if (winHwnd == (long)topLevelHwnd || winHwnd == (long)hwnd)
                    {
                        dynamic? doc = window.Document;
                        if (doc == null) continue;

                        dynamic? selectedItems = doc.SelectedItems();
                        if (selectedItems != null && selectedItems.Count > 0)
                        {
                            dynamic? item = selectedItems.Item(0);
                            if (item != null)
                            {
                                string? path = item.Path;
                                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                                {
                                    Log.Information("Selection retrieved via fallback Document.SelectedItems: {Path}", path);
                                    return path;
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not retrieve Explorer selected file");
        }
        return null;
    }

    public static List<string> GetSelectedFilePathsInExplorer()
    {
        var paths = new List<string>();
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return paths;

            string focusedClass = GetWindowClassName(hwnd);
            if (focusedClass.Contains("Edit", StringComparison.OrdinalIgnoreCase))
                return paths;

            IntPtr topLevelHwnd = GetAncestor(hwnd, GA_ROOT);
            if (topLevelHwnd == IntPtr.Zero) topLevelHwnd = hwnd;

            string topClass = GetWindowClassName(topLevelHwnd);
            bool isExplorer = topClass == "CabinetWClass" || topClass == "ExplorerWClass" ||
                              focusedClass == "CabinetWClass" || focusedClass == "DirectUIHWND" ||
                              focusedClass == "SysListView32" || focusedClass == "ShellTabWindowClass";

            bool isDesktop = topClass == "Progman" || topClass == "WorkerW" || focusedClass == "Progman" || focusedClass == "WorkerW";

            if (!isExplorer && !isDesktop)
                return paths;

            string topTitle = GetWindowTitle(topLevelHwnd);

            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return paths;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return paths;

            dynamic windows = shell.Windows();
            int count = windows.Count;

            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window == null) continue;

                try
                {
                    long winHwnd = (long)window.HWND;
                    if (winHwnd == (long)topLevelHwnd || winHwnd == (long)hwnd)
                    {
                        string locName = window.LocationName ?? string.Empty;
                        if (!string.IsNullOrEmpty(locName) &&
                            (topTitle.StartsWith(locName, StringComparison.OrdinalIgnoreCase) ||
                             topTitle.Contains(locName, StringComparison.OrdinalIgnoreCase)))
                        {
                            dynamic? doc = window.Document;
                            if (doc != null)
                            {
                                dynamic? selectedItems = doc.SelectedItems();
                                if (selectedItems != null && selectedItems.Count > 0)
                                {
                                    for (int j = 0; j < selectedItems.Count; j++)
                                    {
                                        dynamic? item = selectedItems.Item(j);
                                        if (item != null)
                                        {
                                            string? p = item.Path;
                                            if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                                                paths.Add(p);
                                        }
                                    }
                                    if (paths.Count > 0) return paths;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window == null) continue;

                try
                {
                    long winHwnd = (long)window.HWND;
                    if (winHwnd == (long)topLevelHwnd || winHwnd == (long)hwnd)
                    {
                        dynamic? doc = window.Document;
                        if (doc == null) continue;

                        dynamic? selectedItems = doc.SelectedItems();
                        if (selectedItems != null && selectedItems.Count > 0)
                        {
                            for (int j = 0; j < selectedItems.Count; j++)
                            {
                                dynamic? item = selectedItems.Item(j);
                                if (item != null)
                                {
                                    string? p = item.Path;
                                    if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                                        paths.Add(p);
                                }
                            }
                            if (paths.Count > 0) return paths;
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not retrieve Explorer selected files");
        }

        return paths;
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return string.Empty;
        var builder = new StringBuilder(256);
        GetClassName(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return string.Empty;
        var builder = new StringBuilder(256);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }
}
