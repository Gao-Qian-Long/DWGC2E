using DwgTranslator.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// Shell navigation and the dashboard numbers shown next to the work area.
///
/// The interface follows the product design: a sidebar with 图纸翻译 / 任务中心 / 术语库 / 会员中心 /
/// an account strip, and per-page statistics. Everything here binds to data the application
/// already owns — nothing is a placeholder that would mislead the user.
/// </summary>
public partial class MainViewModel
{
    public const string PageTranslate = "translate";
    public const string PageBatch = "batch";
    public const string PageGlossary = "glossary";
    public const string PageAccount = "account";
    public const string PageSettings = "settings";
    public bool IsSettingsPage => CurrentPage == PageSettings;

    [ObservableProperty] private string _currentPage = PageTranslate;

    public bool IsTranslatePage => CurrentPage == PageTranslate;
    public bool IsBatchPage => CurrentPage == PageBatch;
    public bool IsGlossaryPage => CurrentPage == PageGlossary;
    public bool IsAccountPage => CurrentPage == PageAccount;

    private bool _restoringPage;
    private string _acceptedPage = PageTranslate;
    partial void OnCurrentPageChanged(string value)
    {
        if (_restoringPage) return;
        if (_acceptedPage != value && !ConfirmLeavePage())
        {
            _restoringPage = true; CurrentPage = _acceptedPage; _restoringPage = false;
            return;
        }
        _acceptedPage = value;
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsTranslatePage));
        OnPropertyChanged(nameof(IsBatchPage));
        OnPropertyChanged(nameof(IsGlossaryPage));
        OnPropertyChanged(nameof(IsAccountPage));
        RefreshPageStatistics();
        if (CurrentPage == PageAccount) _ = SafeRefreshAccountAsync();
        if (CurrentPage == PageGlossary) LoadTermEditor();
    }

    [RelayCommand]
    private void NavigateTo(string? page)
    {
        if (string.IsNullOrWhiteSpace(page)) return;
        if (page == "output-settings") { CurrentPage = PageSettings; SettingsSection = 2; return; }
        CurrentPage = page;
    }

    /// <summary>
    /// Opens one drawing's text entities: selects it as the edited drawing and switches to the
    /// batch page, which is where the per-entity table (and the 校对 workflow) lives now that the
    /// translate page is a per-drawing task table.
    /// </summary>
    [RelayCommand]
    private void OpenDrawingFileItems(DrawingFileItem? item)
    {
        if (item == null || !ConfirmLeaveProofreading()) return;
        SelectedDrawingFile = item;
        ApplyFilter();
        CurrentPage = PageBatch;
        if (CurrentPage == PageBatch) { SelectedBatchTask = item; IsProofreading = true; IsTaskDetailOpen = false; }
    }

    /// <summary>True when the workspace has at least one drawing (drives the empty hint).</summary>
    [ObservableProperty] private bool _hasDrawingFiles;

    /// <summary>Translations produced in this session, in characters — the design's "今日翻译".</summary>
    [ObservableProperty] private int _translatedCharacterCount;

    /// <summary>Labels the CAD writer had to scale down to make them fit (from the plugin log).</summary>
    [ObservableProperty] private int _autoScaledLabelCount;

    /// <summary>Collisions the CAD writer resolved by shrinking a label (from the plugin log).</summary>
    [ObservableProperty] private int _interferenceResolvedCount;

    [ObservableProperty] private string _etaText = string.Empty;

    /// <summary>Share of finished rows that did not fail; "—" until there is anything to measure.</summary>
    public string SuccessRateText
    {
        get
        {
            int done = Entities.Count(e => e.Status is TranslationStatus.Translated
                or TranslationStatus.Reviewed or TranslationStatus.WritebackSuccess);
            int failed = Entities.Count(e => e.Status is TranslationStatus.TranslationFailed
                or TranslationStatus.WritebackFailed);
            int total = done + failed;
            return total == 0 ? "—" : $"{done * 100.0 / total:0}%";
        }
    }

    /// <summary>Where exported drawings are written by default (the design's 输出设置 card).</summary>
    public string OutputDirectoryText => _config.ExportDirectory;

    public string OutputFormatText => "DWG / DXF（与源文件格式一致）";

    public string MachineName => Environment.MachineName;

    public string QuotaText => IsLicensingEnabled
        ? LicenseStatusText
        : "未启用授权（不限次数）";

    public string QuotaHintText => IsLicensingEnabled
        ? "授权按次数计费，导出成功后扣减一次"
        : "当前构建未启用授权体系，翻译与导出不计数";

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_config.ExportDirectory))
            {
                // 还没导出过：默认目录是"源图纸旁边"，队列里有图纸就直接给它算出来并落盘，
                // 而不是让用户先去设置里选一次（旧默认值在 AppData 里，那条路已经作废）。
                ResolveExportDirectory();
                if (string.IsNullOrWhiteSpace(_config.ExportDirectory))
                {
                    ToastService.Warning("还没有可打开的输出目录：请先导出一次，或先在“文件与输出”中选择路径。");
                    return;
                }
                OnPropertyChanged(nameof(OutputDirectoryText));
                OnPropertyChanged(nameof(WorkspaceActionHintText));
                OnPropertyChanged(nameof(WorkspaceOutputLocationText));
                OnPropertyChanged(nameof(ShellStatusText));
            }
            var parent = Path.GetDirectoryName(_config.ExportDirectory);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)) Directory.CreateDirectory(_config.ExportDirectory);
            if (OpenFolder(_config.ExportDirectory)) ToastService.Info("输出目录已打开。");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening the output folder failed");
            ToastService.Warning("无法打开输出目录，请检查路径和访问权限后重试。");
        }
    }

    /// <summary>Recomputes the dashboard from the workspace and the newest CAD plugin log.</summary>
    [RelayCommand]
    private void RefreshPageStatistics()
    {
        TranslatedCharacterCount = Entities
            .Where(e => e.Status is TranslationStatus.Translated or TranslationStatus.Reviewed
                or TranslationStatus.WritebackSuccess)
            .Sum(e => e.TranslatedText?.Length ?? 0);

        OnPropertyChanged(nameof(SuccessRateText));
        OnPropertyChanged(nameof(OutputDirectoryText));
        OnPropertyChanged(nameof(QuotaText));
        OnPropertyChanged(nameof(QuotaHintText));
        RefreshCadWritebackCounters();
    }

    /// <summary>
    /// Reads the two layout metrics the design shows from the newest CAD plugin log.
    ///
    /// They are produced by the writer itself ("uniform scale to 91% of the original size",
    /// "resolved by shrinking") and used to be visible only inside the log text; surfacing them is
    /// how a user sees that the layout pass actually did something.
    /// </summary>
    private void RefreshCadWritebackCounters()
    {
        try
        {
            var logDirectory = Path.Combine(App.AppDataDir, "logs");
            if (!Directory.Exists(logDirectory)) { AutoScaledLabelCount = 0; InterferenceResolvedCount = 0; return; }

            var newest = new DirectoryInfo(logDirectory).GetFiles("cad_plugin_*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) { AutoScaledLabelCount = 0; InterferenceResolvedCount = 0; return; }

            int scaled = 0, interference = 0;
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("uniform scale to", StringComparison.Ordinal)) scaled++;
                else if (line.Contains("resolved by shrinking", StringComparison.Ordinal)) interference++;
            }
            AutoScaledLabelCount = scaled;
            InterferenceResolvedCount = interference;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "CAD writeback counters unavailable");
        }
    }

    /// <summary>Records when the current long-running operation started, for the remaining-time label.</summary>
    private DateTime _operationStartedUtc = DateTime.UtcNow;

    private void MarkOperationStarted()
    {
        _operationStartedUtc = DateTime.UtcNow;
        EtaText = string.Empty;
    }

    /// <summary>
    /// Remaining time from the progress actually achieved so far; stays empty while the estimate
    /// would be noise (below 5% there is not enough information to be honest about it).
    /// </summary>
    private void UpdateEta(double percent)
    {
        if (percent < 5 || percent >= 100) { EtaText = string.Empty; return; }
        var elapsed = DateTime.UtcNow - _operationStartedUtc;
        if (elapsed.TotalSeconds < 2) { EtaText = string.Empty; return; }

        var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - percent) / percent);
        EtaText = remaining.TotalMinutes >= 1
            ? $"预计剩余约 {Math.Ceiling(remaining.TotalMinutes)} 分钟"
            : $"预计剩余约 {Math.Ceiling(remaining.TotalSeconds)} 秒";
    }

    public string StatusReadyText => Strings.Get("StatusReady");
}
