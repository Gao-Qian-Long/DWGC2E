using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// ViewModel for the log viewer panel.
/// Displays real-time log entries with level/source filtering, search, and export.
/// </summary>
public partial class LogViewModel : ObservableObject, IDisposable
{
    private readonly ILogStore _logStore;
    private readonly int _maxDisplayEntries = 500;

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _selectedLevelFilter = "ALL";
    [ObservableProperty] private string _selectedSourceFilter = "ALL";
    [ObservableProperty] private string _searchFilter = string.Empty;
    [ObservableProperty] private int _totalLogCount;
    [ObservableProperty] private string _levelSummary = string.Empty;
    [ObservableProperty] private LogEntryViewModel? _selectedEntry;

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();
    public string[] LevelFilters { get; } = { "ALL", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" };
    public string[] SourceFilters { get; } = { "ALL", "APP", "CAD", "XLT" };

    public LogViewModel(ILogStore logStore)
    {
        _logStore = logStore;
        _logStore.EntryAdded += OnEntryAdded;
        RefreshLevelSummary();
    }

    public LogViewModel() : this(App.LogStore ?? new InMemoryLogStore()) { }

    [RelayCommand]
    private void ToggleVisibility()
    {
        IsVisible = !IsVisible;
        if (IsVisible) RefreshEntries();
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused) RefreshEntries();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logStore.Clear();
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LogEntries.Clear();
            TotalLogCount = 0;
            LevelSummary = string.Empty;
        });
    }

    [RelayCommand]
    private void CopyLogs()
    {
        var text = string.Join("\n", LogEntries.Select(e => e.DisplayText));
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    [RelayCommand]
    private void ExportLogs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "日志文件|*.log|文本文件|*.txt|所有文件|*.*",
            Title = "导出运行日志",
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
            DwgTranslator.App.Views.PromptDialog.Show($"导出日志失败：{ex.Message.Replace('\\', '/')}", "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var logDir = Path.Combine(App.AppDataDir, "logs");
        if (Directory.Exists(logDir))
            System.Diagnostics.Process.Start("explorer.exe", logDir);
    }

    partial void OnSelectedLevelFilterChanged(string value) => RefreshEntries();
    partial void OnSelectedSourceFilterChanged(string value) => RefreshEntries();
    partial void OnSearchFilterChanged(string value) => RefreshEntries();

    private void OnEntryAdded(LogEntry entry)
    {
        if (!IsVisible || IsPaused) return;
        if (!PassesFilter(entry)) return;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LogEntries.Add(new LogEntryViewModel(entry));
            TotalLogCount = _logStore.Count;

            while (LogEntries.Count > _maxDisplayEntries)
                LogEntries.RemoveAt(0);

            RefreshLevelSummary();
        });
    }

    private void RefreshEntries()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
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
        "DEBUG" => LogLevel.Debug, "INFO" => LogLevel.Information,
        "WARN" => LogLevel.Warning, "ERROR" => LogLevel.Error,
        "FATAL" => LogLevel.Fatal, _ => LogLevel.Verbose
    };

    private LogSource? GetSourceFilter() => SelectedSourceFilter switch
    {
        "APP" => LogSource.App, "CAD" => LogSource.CadPlugin,
        "XLT" => LogSource.Translation, _ => null
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
