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
    private LogViewModel? _subscribedViewModel;

    public LogViewerPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is LogViewModel vm)
        {
            if (ReferenceEquals(_subscribedViewModel, vm)) return;
            if (_subscribedViewModel != null)
                _subscribedViewModel.LogEntries.CollectionChanged -= OnLogEntriesChanged;
            _subscribedViewModel = vm;
            vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedViewModel != null)
            _subscribedViewModel.LogEntries.CollectionChanged -= OnLogEntriesChanged;
        _subscribedViewModel = null;
    }

    private void OnLogEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_subscribedViewModel?.AutoScroll == true && LogListView.Items.Count > 0)
            LogListView.ScrollIntoView(LogListView.Items[^1]);
    }
    private void LogListView_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_subscribedViewModel == null || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            return;

        var atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1;
        if (_subscribedViewModel.AutoScroll != atBottom)
            _subscribedViewModel.AutoScroll = atBottom;
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
        return System.Windows.Data.Binding.DoNothing;
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
        return System.Windows.Data.Binding.DoNothing;
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
        return System.Windows.Data.Binding.DoNothing;
    }
}
