using System;
using System.Globalization;
using System.Windows.Data;

namespace SmartSaver.Converters;

/// <summary>
/// Allows entering decimal values like ₹2.5 or ₹1.75 in TextBox without WPF reverting on partial input.
/// Returns Binding.DoNothing when the user is mid-typing (e.g. "2." or "-") to let the UI keep
/// showing the in-progress text without prematurely overwriting or reverting.
/// </summary>
[ValueConversion(typeof(double), typeof(string))]
public class DoubleStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d)
            return d.ToString("G", CultureInfo.InvariantCulture);
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return 0.0;

        s = s.Trim().Replace(',', '.');

        // Allow partial input while user is still typing decimal point
        if (s == "-" || s == "." || s.EndsWith(".", StringComparison.Ordinal))
            return System.Windows.Data.Binding.DoNothing;

        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            return result;

        return System.Windows.Data.Binding.DoNothing;
    }
}
