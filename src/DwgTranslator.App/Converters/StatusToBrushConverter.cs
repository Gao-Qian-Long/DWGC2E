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
    // Soft professional colors (no deep blues/reds)
    private static readonly SolidColorBrush PendingBrush = new(Color.FromRgb(0x9E, 0x9E, 0x9E));         // gray
    private static readonly SolidColorBrush GlossaryBrush = new(Color.FromRgb(0xFF, 0xB7, 0x4D));       // soft orange
    private static readonly SolidColorBrush TranslatedBrush = new(Color.FromRgb(0x66, 0xBB, 0x6A));      // soft green
    private static readonly SolidColorBrush ReviewedBrush = new(Color.FromRgb(0x43, 0xA0, 0x47));         // medium green
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(0xEF, 0x9A, 0x9A));          // soft red
    private static readonly SolidColorBrush WritebackSuccessBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));// dark green
    private static readonly SolidColorBrush WritebackFailedBrush = new(Color.FromRgb(0xE5, 0x73, 0x73)); // red
    private static readonly SolidColorBrush SkippedBrush = new(Color.FromRgb(0xBD, 0xBD, 0xBD));         // light gray
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(0x75, 0x75, 0x75));         // dark gray

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
        throw new NotImplementedException();
    }
}