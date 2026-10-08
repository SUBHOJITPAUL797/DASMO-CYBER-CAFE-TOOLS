using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SmartSaver.Helpers;

/// <summary>
/// Natural string comparer matching Windows Explorer file ordering (e.g. File 1, File 2, File 10).
/// Uses the native Windows StrCmpLogicalW API.
/// </summary>
public class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        try
        {
            return StrCmpLogicalW(x, y);
        }
        catch
        {
            return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
