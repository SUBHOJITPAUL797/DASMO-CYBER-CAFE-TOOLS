using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Generates an immutable, tamper-resistant physical hardware fingerprint for 1-PC-1-Account licensing.
/// Combines Windows MachineGuid, BIOS/Motherboard hardware signatures, and Processor architecture.
/// </summary>
public static class HardwareIdService
{
    private static string? _cachedHardwareId;
    private static string? _cachedDeviceModel;

    /// <summary>
    /// Returns the unique physical hardware ID for this PC (e.g. PC-A1B2-C3D4-E5F6-7890).
    /// </summary>
    public static string GetHardwareId()
    {
        if (!string.IsNullOrEmpty(_cachedHardwareId))
            return _cachedHardwareId;

        try
        {
            var sb = new StringBuilder();

            // 1. Windows Cryptography MachineGuid
            string machineGuid = string.Empty;
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                machineGuid = key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
            }
            catch { }

            if (string.IsNullOrEmpty(machineGuid))
            {
                try
                {
                    using var key32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                        .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                    machineGuid = key32?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
                }
                catch { }
            }
            sb.Append("GUID:").Append(machineGuid).Append(';');

            // 2. BIOS / Motherboard hardware profile from Registry (no admin rights needed)
            try
            {
                using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                if (biosKey != null)
                {
                    sb.Append("MFR:").Append(biosKey.GetValue("SystemManufacturer")?.ToString()).Append(';');
                    sb.Append("PROD:").Append(biosKey.GetValue("SystemProductName")?.ToString()).Append(';');
                    sb.Append("BOARD:").Append(biosKey.GetValue("BaseBoardProduct")?.ToString()).Append(';');
                    sb.Append("BIOS:").Append(biosKey.GetValue("BIOSVersion")?.ToString()).Append(';');
                }
            }
            catch { }

            // 3. Processor & System Identity
            sb.Append("PROC:").Append(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty).Append(';');
            sb.Append("CORES:").Append(Environment.ProcessorCount).Append(';');
            sb.Append("NAME:").Append(Environment.MachineName).Append(';');

            // Hash the combined hardware profile using SHA-256
            byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            string hex = Convert.ToHexString(hashBytes); // 64 chars

            // Format as readable PC-XXXX-XXXX-XXXX-XXXX
            _cachedHardwareId = $"PC-{hex.Substring(0, 4)}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}";
            return _cachedHardwareId;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to compute hardware ID. Falling back to machine name hash.");
            byte[] fallback = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "_DASMO_FALLBACK"));
            string hex = Convert.ToHexString(fallback);
            _cachedHardwareId = $"PC-{hex.Substring(0, 4)}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}";
            return _cachedHardwareId;
        }
    }

    /// <summary>
    /// Returns a human-friendly device name (e.g. "SUBHOJIT-PC (Windows 11 x64)").
    /// </summary>
    public static string GetDeviceModel()
    {
        if (!string.IsNullOrEmpty(_cachedDeviceModel))
            return _cachedDeviceModel;

        string osName = Environment.OSVersion.Version.Major >= 10 ? "Windows 10/11" : "Windows";
        _cachedDeviceModel = $"{Environment.MachineName} ({osName} {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})";
        return _cachedDeviceModel;
    }

    #region Silent Full PC Hardware Specs Extraction

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX() { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX lpBuffer);

    public static string GetTotalRamString()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                double gb = memStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
                return $"{Math.Round(gb, 1)} GB (approx. {Math.Ceiling(gb)} GB)";
            }
        }
        catch { }
        return "Unknown RAM";
    }

    public static string GetCpuString()
    {
        try
        {
            using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? name = cpuKey?.GetValue("ProcessorNameString")?.ToString()?.Trim();
            int cores = Environment.ProcessorCount;
            if (!string.IsNullOrEmpty(name))
            {
                return $"{name} ({cores} Cores)";
            }
        }
        catch { }
        return $"Processor ({Environment.ProcessorCount} Cores)";
    }

    public static string GetOsString()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string prod = key?.GetValue("ProductName")?.ToString() ?? "Windows";
            string displayVer = key?.GetValue("DisplayVersion")?.ToString() ?? "";
            string build = key?.GetValue("CurrentBuildNumber")?.ToString() ?? Environment.OSVersion.Version.Build.ToString();
            string arch = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";

            if (int.TryParse(build, out int bNum) && bNum >= 22000 && prod.Contains("Windows 10"))
            {
                prod = prod.Replace("Windows 10", "Windows 11");
            }

            string verTag = string.IsNullOrEmpty(displayVer) ? "" : $", {displayVer}";
            return $"{prod} {arch} (Build {build}{verTag})";
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    public static string GetScreenResolution()
    {
        try
        {
            int w = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
            int h = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
            if (w > 0 && h > 0)
                return $"{w} x {h} px";
        }
        catch { }
        return "Unknown Display";
    }

    public static string GetMotherboardString()
    {
        try
        {
            using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (biosKey != null)
            {
                string mfr = biosKey.GetValue("SystemManufacturer")?.ToString()?.Trim() ?? "";
                string board = biosKey.GetValue("BaseBoardProduct")?.ToString()?.Trim() ?? "";
                string bios = biosKey.GetValue("BIOSVersion")?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(board))
                    return $"{mfr} {board} (BIOS: {bios})".Trim();
            }
        }
        catch { }
        return "Standard System Board";
    }

    public static string GetWindowsUserString()
    {
        return $"{Environment.UserName} (PC: {Environment.MachineName})";
    }

    public static string GetLocalIpAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public static ComputerSpecs GetComputerSpecs()
    {
        return new ComputerSpecs
        {
            Processor = GetCpuString(),
            RamTotal = GetTotalRamString(),
            OsVersion = GetOsString(),
            ScreenResolution = GetScreenResolution(),
            Motherboard = GetMotherboardString(),
            WindowsUser = GetWindowsUserString(),
            LocalIp = GetLocalIpAddress(),
            DeviceId = GetHardwareId(),
            MachineName = Environment.MachineName
        };
    }

    #endregion
}

public class ComputerSpecs
{
    public string Processor { get; set; } = string.Empty;
    public string RamTotal { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string ScreenResolution { get; set; } = string.Empty;
    public string Motherboard { get; set; } = string.Empty;
    public string WindowsUser { get; set; } = string.Empty;
    public string LocalIp { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"OS: {OsVersion}\nCPU: {Processor}\nRAM: {RamTotal}\nDisplay: {ScreenResolution}\nBoard: {Motherboard}\nUser: {WindowsUser}\nIP: {LocalIp}\nHWID: {DeviceId}";
    }
}
