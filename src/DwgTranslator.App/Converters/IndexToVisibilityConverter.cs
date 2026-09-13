using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DwgTranslator.App.Converters;

/// <summary>
/// Shows a settings section only while its entry is selected in the left sub-navigation.
/// Bind to the nav's SelectedIndex and pass the section ordinal as ConverterParameter —
/// without this the sub-nav is decorative and every section is on screen at once.
/// </summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int selected = value is int i ? i : 0;
        if (selected < 0) selected = 0;                       // 尚未选中时按第一项处理

        int index = 0;
        if (parameter != null && int.TryParse(parameter.ToString(), out var p)) index = p;

        return selected == index ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}