using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DwgTranslator.App.Converters;

/// <summary>
/// Converts boolean values to Visibility (and vice versa).
/// When targetType is string, returns "✅" for true and "" for false (for glossary hit display).
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;

        // If parameter is "false" or "invert", negate the boolean
        if (parameter is string p && (p == "false" || p == "invert" || p == "negate"))
            b = !b;

        // If caller expects a string (e.g. DataGridTextColumn), return emoji
        if (targetType == typeof(string))
            return b ? "✅" : "";

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility vis)
            return vis == Visibility.Visible;
        if (value is string s)
            return s == "✅";
        return false;
    }
}