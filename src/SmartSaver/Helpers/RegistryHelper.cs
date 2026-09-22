using System.IO;
using Microsoft.Win32;
using Serilog;

namespace SmartSaver.Helpers;

/// <summary>
/// Manages Windows Registry entries for startup and shell context menu integration
/// </summary>
public static class RegistryHelper
{
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DASMO CYBER CAFE TOOLS";

    /// <summary>
    /// Shell context menu registry paths for each supported image extension
    /// </summary>
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif" };

    /// <summary>
    /// Enables or disables the "Start with Windows" registry entry
    /// </summary>
    public static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            if (key == null)
            {
                Log.Warning("Could not open startup registry key");
                return;
            }

            // Always delete legacy keys to ensure clean display in Task Manager
            key.DeleteValue("SmartSaver", throwOnMissingValue: false);
            key.DeleteValue("SmartSaver.exe", throwOnMissingValue: false);
            key.DeleteValue("DASMO CYBER COMPRESSOR", throwOnMissingValue: false);

            if (enabled)
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(AppContext.BaseDirectory, "DASMO CYBER CAFE TOOLS.exe");
                key.SetValue(AppName, $"\"{exePath}\" --background");
                Log.Information("Startup registry entry added: {ExePath}", exePath);
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                Log.Information("Startup registry entry removed");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update startup registry entry");
        }
    }

    /// <summary>
    /// Checks if the application is currently set to start with Windows
    /// </summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey);
            return key?.GetValue(AppName) != null || key?.GetValue("DASMO CYBER COMPRESSOR") != null || key?.GetValue("SmartSaver") != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers the shell context menu entries for all image file types.
    /// Adds "SmartSaver → Resize / Compress Image" to the right-click menu.
    /// </summary>
    public static void RegisterContextMenu()
    {
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "DASMO CYBER CAFE TOOLS.exe");

            // Clean up any legacy SmartSaver context menu keys from the user registry
            try
            {
                foreach (string ext in ImageExtensions)
                {
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{ext}\shell\SmartSaver", throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{ext}\shell\SmartSaverStamp", throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{ext}\shell\SmartSaverEnhance", throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{ext}\shell\SmartSaverImg2Pdf", throwOnMissingSubKey: false);
                }
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\SmartSaverMerge", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\SmartSaverSplit", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\SmartSaverPdf2Img", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\SmartSaverPdfEdit", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\SmartSaverCompress", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\SmartSaverPeek", throwOnMissingSubKey: false);
            }
            catch { }

            foreach (string ext in ImageExtensions)
            {
                RegisterContextMenuForExtension(ext, exePath);
                RegisterImageToPdfContextMenu(ext, exePath);
                RegisterScanEnhanceContextMenu(ext, exePath);
                RegisterPhotoStampContextMenu(ext, exePath);
            }

            RegisterPdfMergeContextMenu();
            RegisterPdfSplitContextMenu();
            RegisterPdfToImageContextMenu();
            RegisterPdfEditContextMenu();

            Log.Information("Shell context menu registered for all tools");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to register shell context menu");
        }
    }

    /// <summary>
    /// Registers Candidate Photo Stamp context menu for a specific image extension
    /// </summary>
    public static void RegisterPhotoStampContextMenu(string extension, string exePath)
    {
        try
        {
            string keyPath = $@"Software\Classes\SystemFileAssociations\{extension}\shell\DasmoStamp";
            using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
            if (shellKey != null)
            {
                shellKey.SetValue("", "DASMO CYBER CAFE TOOLS - Add Name & Date Stamp (SSC/Exam)...");
                shellKey.SetValue("Icon", $"\"{exePath}\",0");
                shellKey.SetValue("Position", "Top");

                using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
                commandKey?.SetValue("", $"\"{exePath}\" --stamp \"%1\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register PhotoStamp context menu for {Ext}", extension);
        }
    }

    /// <summary>
    /// Registers Scan Enhancer context menu for a specific image extension
    /// </summary>
    public static void RegisterScanEnhanceContextMenu(string extension, string exePath)
    {
        try
        {
            string keyPath = $@"Software\Classes\SystemFileAssociations\{extension}\shell\DasmoEnhance";
            using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
            if (shellKey != null)
            {
                shellKey.SetValue("", "DASMO CYBER CAFE TOOLS - Enhance Document Scan...");
                shellKey.SetValue("Icon", $"\"{exePath}\",0");
                shellKey.SetValue("Position", "Top");

                using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
                commandKey?.SetValue("", $"\"{exePath}\" --enhance \"%1\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register Scan Enhance context menu for {Ext}", extension);
        }
    }

    /// <summary>
    /// Registers Image to PDF context menu for a specific image extension
    /// </summary>
    public static void RegisterImageToPdfContextMenu(string extension, string exePath)
    {
        try
        {
            string keyPath = $@"Software\Classes\SystemFileAssociations\{extension}\shell\DasmoImg2Pdf";
            using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
            if (shellKey != null)
            {
                shellKey.SetValue("", "DASMO CYBER CAFE TOOLS - Convert to PDF (A4)...");
                shellKey.SetValue("Icon", $"\"{exePath}\",0");
                shellKey.SetValue("Position", "Top");

                using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
                commandKey?.SetValue("", $"\"{exePath}\" --img2pdf \"%1\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register Image2Pdf context menu for {Ext}", extension);
        }
    }

    /// <summary>
    /// Registers PDF to Image context menu
    /// </summary>
    public static void RegisterPdfToImageContextMenu()
    {
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "DASMO CYBER CAFE TOOLS.exe");

            string keyPath = @"Software\Classes\SystemFileAssociations\.pdf\shell\DasmoPdf2Img";
            using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
            if (shellKey != null)
            {
                shellKey.SetValue("", "DASMO CYBER CAFE TOOLS - Convert PDF to Images...");
                shellKey.SetValue("Icon", $"\"{exePath}\",0");
                shellKey.SetValue("Position", "Top");

                using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
                commandKey?.SetValue("", $"\"{exePath}\" --pdf2img \"%1\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register Pdf2Img context menu");
        }
    }

    /// <summary>
    /// Registers the right-click context menu option for merging PDFs
    /// </summary>
    public static void RegisterPdfMergeContextMenu()
    {
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "DASMO CYBER CAFE TOOLS.exe");

            string keyPath = @"Software\Classes\SystemFileAssociations\.pdf\shell\DasmoMerge";

            using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
            if (shellKey != null)
            {
                shellKey.SetValue("", "DASMO CYBER CAFE TOOLS - Merge PDFs...");
                shellKey.SetValue("Icon", $"\"{exePath}\",0");
                shellKey.SetValue("Position", "Top");

                using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
                commandKey?.SetValue("", $"\"{exePath}\" --merge \"%1\"");
            }

            Log.Information("Shell context menu registered for PDF merge");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to register PDF merge context menu");
        }
    }

    /// <summary>
    /// Registers the right-click context menu option for extracting and splitting PDF pages
    /// </summary>
    public static void RegisterPdfSplitContextMenu()
    {
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "DASMO CYBER CAFE TOOLS.exe");

            string keyPath = @"Software\Classes\SystemFileAssociations\.pdf\shell\DasmoSplit";

            using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
            if (shellKey != null)
            {
                shellKey.SetValue("", "DASMO CYBER CAFE TOOLS - Extract / Split Pages...");
                shellKey.SetValue("Icon", $"\"{exePath}\",0");
                shellKey.SetValue("Position", "Top");

                using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
                commandKey?.SetValue("", $"\"{exePath}\" --split \"%1\"");
            }

            Log.Information("Shell context menu registered for PDF split");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to register PDF split context menu");
        }
    }

    /// <summary>
    /// Registers the right-click context menu option for editing PDF in-place (vector text, whiteout, images)
    /// </summary>
    public static void RegisterPdfEditContextMenu()
    {
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "DASMO CYBER CAFE TOOLS.exe");

            string keyPath = @"Software\Classes\SystemFileAssociations\.pdf\shell\DasmoPdfEdit";

            using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
            if (shellKey != null)
            {
                shellKey.SetValue("", "DASMO CYBER CAFE TOOLS - Edit PDF (In-Place)...");
                shellKey.SetValue("Icon", $"\"{exePath}\",0");
                shellKey.SetValue("Position", "Top");

                using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
                commandKey?.SetValue("", $"\"{exePath}\" --edit-pdf \"%1\"");
            }

            Log.Information("Shell context menu registered for PDF edit");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to register PDF edit context menu");
        }
    }

    /// <summary>
    /// Unregisters all shell context menu entries
    /// </summary>
    public static void UnregisterContextMenu()
    {
        try
        {
            foreach (string ext in ImageExtensions)
            {
                UnregisterContextMenuForExtension(ext);
            }

            // Remove current Dasmo keys
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\DasmoMerge", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\DasmoSplit", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\DasmoPdf2Img", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\DasmoPdfEdit", throwOnMissingSubKey: false);

            // Remove legacy SmartSaver keys
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\SmartSaverMerge", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\SmartSaverSplit", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\SmartSaverPdf2Img", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\SmartSaverPdfEdit", throwOnMissingSubKey: false);

            Log.Information("Shell context menu unregistered");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to unregister shell context menu");
        }
    }

    private static void RegisterContextMenuForExtension(string extension, string exePath)
    {
        try
        {
            string keyPath = $@"Software\Classes\SystemFileAssociations\{extension}\shell\DasmoTools";

            // Create the main menu key
            using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
            if (shellKey == null) return;

            shellKey.SetValue("", "DASMO CYBER CAFE TOOLS - Resize / Compress Image");
            shellKey.SetValue("Icon", $"\"{exePath}\",0");

            // Create the command key
            using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
            if (commandKey == null) return;

            commandKey.SetValue("", $"\"{exePath}\" --resize \"%1\"");

            Log.Debug("Context menu registered for {Extension}", extension);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register context menu for {Extension}", extension);
        }
    }

    private static void UnregisterContextMenuForExtension(string extension)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\DasmoTools", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\DasmoStamp", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\DasmoEnhance", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\DasmoImg2Pdf", throwOnMissingSubKey: false);

            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\SmartSaver", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\SmartSaverStamp", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\SmartSaverEnhance", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\SmartSaverImg2Pdf", throwOnMissingSubKey: false);

            Log.Debug("Context menu unregistered for {Extension}", extension);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to unregister context menu for {Extension}", extension);
        }
    }

    /// <summary>
    /// Configures Windows Registry so Windows Explorer Preview Pane and portal File Open dialogs
    /// render PDF pages natively and disables the "The file you are attempting to preview could harm your computer" warning.
    /// </summary>
    public static bool FixExplorerPdfPreviewHandler()
    {
        try
        {
            // 1. Disable preview handler security warning prompt in Explorer
            using (var advKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
            {
                advKey?.SetValue("DisablePreviewHandlerWarnings", 1, RegistryValueKind.DWord);
            }

            // 2. Register PDF preview handler in HKCU for .pdf and SystemFileAssociations
            string pdfPreviewHandlerGuid = "{3A8496D2-697B-4A58-99A6-D71FC34A5778}"; // Edge / Windows Native PDF Preview Handler

            // Check HKLM for active PDF preview handler GUID
            try
            {
                using var hklmHandlers = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers");
                if (hklmHandlers != null)
                {
                    foreach (var valueName in hklmHandlers.GetValueNames())
                    {
                        string desc = hklmHandlers.GetValue(valueName)?.ToString() ?? "";
                        if (desc.Contains("PDF", StringComparison.OrdinalIgnoreCase))
                        {
                            pdfPreviewHandlerGuid = valueName;
                            break;
                        }
                    }
                }
            }
            catch { }

            // Apply preview handler GUID to .pdf extension in HKCU
            using (var pdfShellex = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}"))
            {
                pdfShellex?.SetValue("", pdfPreviewHandlerGuid);
            }

            using (var sysAssocShellex = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\.pdf\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}"))
            {
                sysAssocShellex?.SetValue("", pdfPreviewHandlerGuid);
            }

            // 3. Unblock PDF files in user's Downloads folder
            string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloadsPath))
            {
                foreach (string pdf in Directory.GetFiles(downloadsPath, "*.pdf", SearchOption.AllDirectories))
                {
                    UnblockFile(pdf);
                }
            }

            Log.Information("Successfully configured Explorer PDF Preview Handler in registry ({Guid}) and unblocked downloaded PDFs", pdfPreviewHandlerGuid);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to configure Explorer PDF Preview Handler");
            return false;
        }
    }

    /// <summary>
    /// Removes Zone.Identifier (Mark of the Web) from internet-downloaded files so Windows Explorer previews them without security prompts.
    /// </summary>
    public static void UnblockFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                string zonePath = filePath + ":Zone.Identifier";
                if (File.Exists(zonePath))
                {
                    File.Delete(zonePath);
                    Log.Debug("Unblocked downloaded file Zone.Identifier: {FilePath}", filePath);
                }
            }
        }
        catch
        {
            // Ignore if Zone stream cannot be accessed or deleted
        }
    }
}
