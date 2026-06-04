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
    // Color token palette -- Tailwind slate/amber/emerald/red
    private static readonly SolidColorBrush PendingBrush = new(Color.FromRgb(0x94, 0xA3, 0xB8));           // slate-400
    private static readonly SolidColorBrush GlossaryBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));           // amber-600
    private static readonly SolidColorBrush TranslatedBrush = new(Color.FromRgb(0x05, 0x96, 0x69));         // emerald-600
    private static readonly SolidColorBrush ReviewedBrush = new(Color.FromRgb(0x04, 0x78, 0x57));           // emerald-700
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));            // red-600
    private static readonly SolidColorBrush WritebackSuccessBrush = new(Color.FromRgb(0x06, 0x5F, 0x46));  // emerald-800
    private static readonly SolidColorBrush WritebackFailedBrush = new(Color.FromRgb(0xB9, 0x1C, 0x1C));  // red-700
    private static readonly SolidColorBrush SkippedBrush = new(Color.FromRgb(0xCB, 0xD5, 0xE1));           // slate-300
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(0x47, 0x55, 0x69));           // slate-600

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