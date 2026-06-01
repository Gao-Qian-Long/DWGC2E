using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// ViewModel for the log viewer panel. Displays real-time log entries from <see cref="ILogStore"/>
/// with level filtering, source filtering, text search, auto-scroll, pause, export, and clear functionality.
/// </summary>
public partial class LogViewModel : ObservableObject, IDisposable
{
    private readonly ILogStore _logStore;
    private readonly int _maxDisplayEntries = 500;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _selectedLevelFilter = "ALL";
    [ObservableProperty] private string _selectedSourceFilter = "ALL";
    [ObservableProperty] private string _searchFilter = string.Empty;
    [ObservableProperty] private int _totalLogCount;
    [ObservableProperty] private string _levelSummary = string.Empty;
    [ObservableProperty] private LogEntryViewModel? _selectedEntry;

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    /// <summary>Level filter options for the ComboBox.</summary>
    public string[] LevelFilters { get; } = { "ALL", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" };

    /// <summary>Source filter options for the ComboBox.</summary>
    public string[] SourceFilters { get; } = { "ALL", "APP", "CAD", "XLT" };

    public LogViewModel(ILogStore logStore)
    {
        _logStore = logStore;
        _logStore.EntryAdded += OnEntryAdded;
        RefreshLevelSummary();
    }

    public LogViewModel() : this(App.LogStore ?? new InMemoryLogStore())
    {
    }

    /// <summary>
    /// Toggle log viewer panel visibility.
    /// </summary>
    [RelayCommand]
    private void ToggleVisibility()
    {
        IsVisible = !IsVisible;
        if (IsVisible)
            RefreshEntries();
    }

    /// <summary>
    /// Toggle pause: when paused, new entries are buffered and not displayed until resumed.
    /// </summary>
    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused)
            RefreshEntries(); // Refresh to catch up on buffered entries
    }

    /// <summary>
    /// Clear all log entries from the store and display.
    /// </summary>
    [RelayCommand]
    private void ClearLogs()
    {
        _logStore.Clear();
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogEntries.Clear();
            TotalLogCount = 0;
            LevelSummary = string.Empty;
        });
    }

    /// <summary>
    /// Copy all visible log entries to clipboard.
    /// </summary>
    [RelayCommand]
    private void CopyLogs()
    {
        var text = string.Join("\n", LogEntries.Select(e => e.DisplayText));
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    /// <summary>
    /// Export visible log entries to a text file.
    /// </summary>
    [RelayCommand]
    private void ExportLogs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Log Files|*.log|Text Files|*.txt|All Files|*.*",
            Title = "Export Logs",
            FileName = $"dwgtranslator_logs_{DateTime.Now:yyyyMMdd_HHmmss}.log"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = LogEntries.Select(e => e.DisplayText);
            File.WriteAllLines(dialog.FileName, lines);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export logs: {ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Open the log directory in Windows Explorer.
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        var logDir = Path.Combine(App.AppDataDir, "logs");
        if (Directory.Exists(logDir))
            System.Diagnostics.Process.Start("explorer.exe", logDir);
    }

    partial void OnSelectedLevelFilterChanged(string value)
    {
        RefreshEntries();
    }

    partial void OnSelectedSourceFilterChanged(string value)
    {
        RefreshEntries();
    }

    partial void OnSearchFilterChanged(string value)
    {
        RefreshEntries();
    }

    private void OnEntryAdded(LogEntry entry)
    {
        if (!IsVisible) return;
        if (IsPaused) return; // Buffer entries while paused
        if (!PassesFilter(entry)) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            LogEntries.Add(new LogEntryViewModel(entry));
            TotalLogCount = _logStore.Count;

            // Trim display if too many entries
            while (LogEntries.Count > _maxDisplayEntries)
                LogEntries.RemoveAt(0);

            RefreshLevelSummary();
        });
    }

    private void RefreshEntries()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogEntries.Clear();
            var minLevel = GetMinLevel();
            var sourceFilter = GetSourceFilter();
            var entries = string.IsNullOrEmpty(SearchFilter)
                ? _logStore.GetFiltered(minLevel, count: _maxDisplayEntries, sourceFilter: sourceFilter)
                : _logStore.GetFiltered(minLevel, SearchFilter, _maxDisplayEntries, sourceFilter: sourceFilter);

            foreach (var entry in entries)
                LogEntries.Add(new LogEntryViewModel(entry));

            TotalLogCount = _logStore.Count;
            RefreshLevelSummary();
        });
    }

    private bool PassesFilter(LogEntry entry)
    {
        var minLevel = GetMinLevel();
        if (entry.Level < minLevel) return false;

        var sourceFilter = GetSourceFilter();
        if (sourceFilter.HasValue && entry.LogSource != sourceFilter.Value) return false;

        if (!string.IsNullOrEmpty(SearchFilter))
        {
            var search = SearchFilter;
            return entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   (entry.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        return true;
    }

    private LogLevel GetMinLevel() => SelectedLevelFilter switch
    {
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Information,
        "WARN" => LogLevel.Warning,
        "ERROR" => LogLevel.Error,
        "FATAL" => LogLevel.Fatal,
        _ => LogLevel.Verbose
    };

    private LogSource? GetSourceFilter() => SelectedSourceFilter switch
    {
        "APP" => LogSource.App,
        "CAD" => LogSource.CadPlugin,
        "XLT" => LogSource.Translation,
        _ => null // "ALL" = no filter
    };

    private void RefreshLevelSummary()
    {
        var counts = new int[6];
        foreach (var entry in LogEntries)
            counts[(int)entry.Level]++;

        var parts = new List<string>();
        if (counts[2] > 0) parts.Add($"INF:{counts[2]}");
        if (counts[3] > 0) parts.Add($"WRN:{counts[3]}");
        if (counts[4] > 0) parts.Add($"ERR:{counts[4]}");
        if (counts[5] > 0) parts.Add($"FTL:{counts[5]}");
        LevelSummary = parts.Count > 0 ? string.Join(" | ", parts) : "No entries";
    }

    public void Dispose()
    {
        _logStore.EntryAdded -= OnEntryAdded;
    }
}

/// <summary>
/// Wrapper around <see cref="LogEntry"/> for WPF binding with computed display properties.
/// </summary>
public class LogEntryViewModel
{
    private readonly LogEntry _entry;

    public LogEntryViewModel(LogEntry entry) => _entry = entry;

    public string Timestamp => _entry.Timestamp.ToString("HH:mm:ss.fff");
    public string LevelTag => _entry.LevelTag;
    public LogLevel Level => _entry.Level;
    public string Category => _entry.Category;
    public string Message => _entry.Message;
    public string? Exception => _entry.Exception;
    public string DisplayText => _entry.DisplayText;
    public string SourceTag => _entry.SourceTag;
    public LogSource LogSource => _entry.LogSource;
    public string? CorrelationId => _entry.CorrelationId;
    public double? DurationMs => _entry.DurationMs;

    /// <summary>
    /// Level badge color hex for XAML binding.
    /// </summary>
    public string LevelColor => _entry.Level switch
    {
        LogLevel.Verbose => "#9E9E9E",
        LogLevel.Debug => "#78909C",
        LogLevel.Information => "#1976D2",
        LogLevel.Warning => "#FF9800",
        LogLevel.Error => "#E53935",
        LogLevel.Fatal => "#B71C1C",
        _ => "#757575"
    };

    /// <summary>
    /// Source badge color hex for XAML binding.
    /// </summary>
    public string SourceColor => _entry.LogSource switch
    {
        LogSource.CadPlugin => "#FF6F00",
        LogSource.Translation => "#7B1FA2",
        _ => "#546E7A"
    };

    /// <summary>
    /// Background color for the log row based on level.
    /// </summary>
    public string RowBackground => _entry.Level switch
    {
        LogLevel.Error => "#FFF0F0",
        LogLevel.Fatal => "#FFEBEE",
        LogLevel.Warning => "#FFFDE7",
        _ => "Transparent"
    };
}
