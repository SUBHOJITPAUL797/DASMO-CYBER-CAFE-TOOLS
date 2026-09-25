using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using Binding = System.Windows.Data.Binding;
using SmartSaver.Models;

namespace SmartSaver.Converters;

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return value;
    }
}

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool bValue = false;
        if (value is bool b)
        {
            bValue = b;
        }
        else if (value is bool?)
        {
            var tmp = (bool?)value;
            bValue = tmp.HasValue ? tmp.Value : false;
        }
        else if (value is int i)
        {
            bValue = i > 0;
        }
        else if (value is long l)
        {
            bValue = l > 0;
        }
        else if (value is double d)
        {
            bValue = d > 0;
        }
        else if (value is string s)
        {
            bValue = !string.IsNullOrWhiteSpace(s);
        }

        bool invert = parameter != null && (parameter.ToString()?.ToLower() == "inverse" || parameter.ToString()?.ToLower() == "invert");

        if (invert)
            bValue = !bValue;

        return bValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isVisible = (value is Visibility v) && v == Visibility.Visible;
        bool invert = parameter != null && (parameter.ToString()?.ToLower() == "inverse" || parameter.ToString()?.ToLower() == "invert");
        return invert ? !isVisible : isVisible;
    }
}

public class StringEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b) return parameter;
        return Binding.DoNothing;
    }
}

public class FileSizeFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return CompressionResult.FormatFileSize(bytes);
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNull = value == null;
        bool invert = parameter != null && (parameter.ToString()?.ToLower() == "inverse" || parameter.ToString()?.ToLower() == "invert" || parameter.ToString()?.ToLower() == "notnull");
        if (invert) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var brush = new System.Windows.Media.BrushConverter().ConvertFromString(hex);
                if (brush != null) return brush;
            }
            catch { }
        }
        if (parameter is string def && !string.IsNullOrWhiteSpace(def))
        {
            try
            {
                var brush = new System.Windows.Media.BrushConverter().ConvertFromString(def);
                if (brush != null) return brush;
            }
            catch { }
        }
        return System.Windows.Media.Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool bValue = false;
        if (value is bool b) bValue = b;
        else if (value != null && bool.TryParse(value.ToString(), out bool parsed)) bValue = parsed;

        return bValue ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v) return v != Visibility.Visible;
        return false;
    }
}


