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

    /// <summary>
    /// Attempts to silently select and scroll to a file in an open Windows Explorer window matching its folder.
    /// Does not steal focus.
    /// </summary>
    public static void SelectFileInExplorer(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            string targetDir = Path.GetDirectoryName(filePath)!;
            string targetName = Path.GetFileName(filePath);

            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            dynamic windows = shell.Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window == null) continue;
                try
                {
                    dynamic? doc = window.Document;
                    if (doc == null) continue;
                    dynamic? folder = doc.Folder;
                    if (folder == null) continue;
                    string winPath = folder.Self?.Path ?? string.Empty;
                    if (string.Equals(winPath, targetDir, StringComparison.OrdinalIgnoreCase))
                    {
                        dynamic? item = folder.ParseName(targetName);
                        if (item != null)
                        {
                            // 1 = SVSI_DESELECTOTHERS, 4 = SVSI_ENSUREVISIBLE, 8 = SVSI_FOCUSED, 16 = SVSI_SELECT
                            doc.SelectItem(item, 1 | 4 | 8 | 16);
                            break;
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not select file in Explorer: {Path}", filePath);
        }
    }

    /// <summary>
    /// Queries the open Windows Explorer window displaying folderPath to retrieve its files in the exact visual order shown to the user (e.g. Date Modified, Grouped, Name).
    /// Returns null if no matching Explorer window is open or if COM access fails.
    /// </summary>
    public static List<string>? GetFolderFilesFromExplorer(string folderPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return null;

            string normalizedTarget = Path.GetFullPath(folderPath).TrimEnd('\\');

            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;

            dynamic windows = shell.Windows();
            int count = windows.Count;

            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window == null) continue;

                try
                {
                    dynamic? doc = window.Document;
                    if (doc == null) continue;

                    dynamic? folder = doc.Folder;
                    if (folder == null) continue;

                    string? winPath = null;
                    try { winPath = folder.Self?.Path; } catch { }

                    if (string.IsNullOrWhiteSpace(winPath))
                    {
                        try
                        {
                            string? locUrl = window.LocationURL;
                            if (!string.IsNullOrWhiteSpace(locUrl) && Uri.TryCreate(locUrl, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                            {
                                winPath = uri.LocalPath;
                            }
                        }
                        catch { }
                    }

                    if (string.IsNullOrWhiteSpace(winPath)) continue;

                    string normalizedWin = Path.GetFullPath(winPath).TrimEnd('\\');
                    if (string.Equals(normalizedTarget, normalizedWin, StringComparison.OrdinalIgnoreCase))
                    {
                        dynamic? items = folder.Items();
                        if (items != null && items.Count > 0)
                        {
                            int countToRead = Math.Min(items.Count, 250);
                            var list = new List<string>(countToRead);
                            for (int j = 0; j < countToRead; j++)
                            {
                                dynamic? it = items.Item(j);
                                string? p = it?.Path;
                                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                                {
                                    list.Add(p);
                                }
                            }
                            if (list.Count > 0)
                            {
                                Log.Information("ExplorerSelectionHelper: Retrieved {Count} items for '{Folder}' from active Explorer view", list.Count, folderPath);
                                return list;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error reading items from Explorer window {Index}", i);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not retrieve folder files from Explorer for {Folder}", folderPath);
        }

        return null;
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

