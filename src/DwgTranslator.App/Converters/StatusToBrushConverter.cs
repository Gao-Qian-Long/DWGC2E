using DwgTranslator.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DwgTranslator.App.Converters;

/// <summary>
/// Converts TranslationStatus to a soft color brush for status badge display.
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    private static SolidColorBrush ThemeBrush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as SolidColorBrush ?? Brushes.Gray;
    private static SolidColorBrush PendingBrush => ThemeBrush("Brush.TextMuted");
    private static SolidColorBrush GlossaryBrush => ThemeBrush("Brush.Warning");
    private static SolidColorBrush TranslatedBrush => ThemeBrush("Brush.Success");
    private static SolidColorBrush ReviewedBrush => ThemeBrush("Brush.Success");
    private static SolidColorBrush FailedBrush => ThemeBrush("Brush.Danger");
    private static SolidColorBrush WritebackSuccessBrush => ThemeBrush("Brush.Success");
    private static SolidColorBrush WritebackFailedBrush => ThemeBrush("Brush.Danger");
    private static SolidColorBrush SkippedBrush => ThemeBrush("Brush.TextMuted");
    private static SolidColorBrush DefaultBrush => ThemeBrush("Brush.TextSecondary");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var brush = value is TranslationStatus status
            ? status switch
            {
                TranslationStatus.Pending => PendingBrush,
                TranslationStatus.GlossaryMatched => GlossaryBrush,
                TranslationStatus.Translated => TranslatedBrush,
                TranslationStatus.Reviewed => ReviewedBrush,
                TranslationStatus.TranslationFailed => FailedBrush,
                TranslationStatus.WritebackSuccess => WritebackSuccessBrush,
                TranslationStatus.WritebackFailed => WritebackFailedBrush,
                TranslationStatus.Skipped => SkippedBrush,
                _ => DefaultBrush
            }
            : DefaultBrush;

        // If target is string, return hex color code for XAML binding
        if (targetType == typeof(string))
            return brush.ToString();

        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
