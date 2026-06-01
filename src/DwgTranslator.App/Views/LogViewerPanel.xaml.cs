using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Logging;

namespace DwgTranslator.App.Views;

/// <summary>
/// Code-behind for the log viewer panel. Handles auto-scroll behavior.
/// </summary>
public partial class LogViewerPanel : UserControl
{
    public LogViewerPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is LogViewModel vm)
        {
            vm.LogEntries.CollectionChanged += (_, _) =>
            {
                if (vm.AutoScroll && LogListView.Items.Count > 0)
                {
                    LogListView.ScrollIntoView(LogListView.Items[^1]);
                }
            };
        }
    }
}

/// <summary>
/// Converts a string to Visibility (Visible if not null/empty, Collapsed otherwise).
/// Used for category and exception display.
/// </summary>
public class StringToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a double? (DurationMs) to Visibility (Visible if has value, Collapsed otherwise).
/// </summary>
public class DurationToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return value is double ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a LogSource to Visibility. Hides the source badge for App sources (to reduce noise),
/// shows it for CAD and Translation sources.
/// </summary>
public class SourceFilterVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is LogSource source)
        {
            return source == LogSource.App ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
