using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Logging;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Serilog;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// ViewModel for the log viewer panel.
/// Displays real-time log entries with level/source filtering, search, and export.
/// </summary>
public partial class LogViewModel : ObservableObject, IDisposable
{
    private readonly ILogStore _logStore;
    private readonly int _maxDisplayEntries = 500;
    private readonly ConcurrentQueue<LogEntry> _pendingEntries = new();
    private int _flushScheduled;

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
    public string[] LevelFilters { get; } = { "ALL", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" };
    public string[] SourceFilters { get; } = { "ALL", "APP", "CAD", "XLT" };
    public bool HasVisibleLogEntries => LogEntries.Count > 0;
    public string EmptyStateTitle => IsPaused ? "日志刷新已暂停" : "暂无运行日志";
    public string EmptyStateDescription => IsPaused
        ? "点击“继续”恢复实时日志；暂停期间产生的日志会在恢复后重新载入。"
        : string.IsNullOrWhiteSpace(SearchFilter) && SelectedLevelFilter == "ALL" && SelectedSourceFilter == "ALL"
            ? "开始导入或翻译后，运行日志会显示在这里。"
            : "当前筛选条件下没有匹配日志，可调整级别、来源或搜索词。";

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
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
        if (!IsPaused) RefreshEntries();
        else DwgTranslator.App.Services.ToastService.Info("日志刷新已暂停。");
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logStore.Clear();
        OnUiThread(() =>
        {
            while (_pendingEntries.TryDequeue(out _)) { }
            LogEntries.Clear();
            TotalLogCount = 0;
            LevelSummary = "No entries";
            NotifyLogPresentationChanged();
            DwgTranslator.App.Services.ToastService.Success("运行日志已清空。");
        });
    }

    [RelayCommand]
    private void CopyLogs()
    {
        var text = string.Join("\n", LogEntries.Select(e => e.DisplayText));
        if (string.IsNullOrEmpty(text))
        {
            DwgTranslator.App.Services.ToastService.Info("当前没有可复制的日志。");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            DwgTranslator.App.Services.ToastService.Success("运行日志已复制。");
        }
        catch (ExternalException)
        {
            DwgTranslator.App.Services.ToastService.Warning("剪贴板暂时不可用，请稍后重试。");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "复制日志失败");
            DwgTranslator.App.Services.ToastService.Warning("复制日志失败，请稍后重试。");
        }
    }

    [RelayCommand]
    private void ExportLogs()
    {
        if (LogEntries.Count == 0)
        {
            DwgTranslator.App.Services.ToastService.Info("当前没有可导出的日志。");
            return;
        }

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
            DwgTranslator.App.Services.ToastService.Success("日志已导出。");
        }
        catch (Exception ex)
        {
            DwgTranslator.App.Services.ToastService.Warning("导出日志失败，请检查目标目录权限后重试。");
            DwgTranslator.App.Views.PromptDialog.Show($"导出日志失败：{ex.Message.Replace('\\', '/')}", "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var logDir = Path.Combine(App.AppDataDir, "logs");
        try
        {
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
            DwgTranslator.App.Services.ToastService.Success("日志目录已打开。");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开日志目录失败");
            DwgTranslator.App.Services.ToastService.Warning("无法打开日志目录，请检查目录权限后重试。");
        }
    }

    partial void OnSelectedLevelFilterChanged(string value) { OnPropertyChanged(nameof(EmptyStateDescription)); RefreshEntries(); }
    partial void OnSelectedSourceFilterChanged(string value) { OnPropertyChanged(nameof(EmptyStateDescription)); RefreshEntries(); }
    partial void OnSearchFilterChanged(string value) { OnPropertyChanged(nameof(EmptyStateDescription)); RefreshEntries(); }

    private void OnEntryAdded(LogEntry entry)
    {
        if (!IsVisible || IsPaused || !PassesFilter(entry)) return;

        _pendingEntries.Enqueue(entry);
        SchedulePendingFlush();
    }

    /// <summary>
    /// 回到 UI 线程。取不到 Application（单元测试 / 设计器）或已经在 UI 线程时直接执行，
    /// 避免裸 <c>Application.Current.Dispatcher</c> 在无消息循环的环境里抛空引用。
    /// </summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) { action(); return; }
        dispatcher.BeginInvoke(action);
    }

    private void SchedulePendingFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) != 0) return;

        _ = Task.Delay(100).ContinueWith(_ =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                Interlocked.Exchange(ref _flushScheduled, 0);
                return;
            }

            dispatcher.BeginInvoke(FlushPendingEntries);
        }, TaskScheduler.Default);
    }

    private void FlushPendingEntries()
    {
        if (!IsVisible || IsPaused)
        {
            while (_pendingEntries.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        while (_pendingEntries.TryDequeue(out var entry))
            LogEntries.Add(new LogEntryViewModel(entry));

        TotalLogCount = _logStore.Count;
        while (LogEntries.Count > _maxDisplayEntries)
            LogEntries.RemoveAt(0);
        RefreshLevelSummary();
        NotifyLogPresentationChanged();

        Interlocked.Exchange(ref _flushScheduled, 0);
        if (!_pendingEntries.IsEmpty && IsVisible && !IsPaused)
            SchedulePendingFlush();
    }

    [RelayCommand]
    private void RefreshEntries()
    {
        OnUiThread(() =>
        {
            while (_pendingEntries.TryDequeue(out _)) { }
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
            NotifyLogPresentationChanged();
        });
    }

    private void NotifyLogPresentationChanged()
    {
        OnPropertyChanged(nameof(HasVisibleLogEntries));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
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
        while (_pendingEntries.TryDequeue(out _)) { }
    }
}
