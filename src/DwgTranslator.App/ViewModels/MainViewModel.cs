using CommunityToolkit.Mvvm.ComponentModel;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Logging;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;
using DwgTranslator.Core.Translation;
using System.Collections.ObjectModel;
using System.Reflection;
using Serilog;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// Main ViewModel for DWG Translator — core skeleton.
/// Partial classes: Config, Translation, ImportExport, Operations, Settings, Tasks, …
///
/// 依赖全部由 DI 注入（构造函数只有一个），不再走 "App.Services?.GetService(...) ?? new ..."
/// 那套服务定位 + 静默兜底：容器没装配好就应该立刻报错，而不是悄悄退化成另一套实现。
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGlossaryService _glossaryService;
    private readonly IExcelService _excelService;
    private readonly IDwgReaderService _dwgReaderService;
    private readonly IDwgWriterService _dwgWriterService;
    private readonly ILicenseService _licenseService;
    private readonly IAutoCadInteropService _autoCadInteropService;
    private readonly ITranslationConsistencyService _consistencyService;
    private readonly ITaskManager _taskManager;
    private readonly TaskManagerOptions _taskOptions;
    private readonly IApiClient _apiClient;
    private readonly IDxfReaderService? _dxfReader;
    private readonly ILogStore? _injectedLogStore;
    private AppConfig _config;
    private string? _settingsPath;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _exportCts;
    private LogViewModel? _logViewModel;

    #region Bindable Properties

    [ObservableProperty] private string _statusMessage = Strings.Get("StatusReady");
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _selectedFilePath = string.Empty;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _translatedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _glossaryHitCount;
    [ObservableProperty] private int _cacheHitCount;
    [ObservableProperty] private string _filterStatusText = Strings.Get("FilterAll");
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private bool _isTopmost;
    [ObservableProperty] private string _operationLabel = string.Empty;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isCancellationRequested;
    [ObservableProperty] private bool _isTranslating;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _languageDirection = Strings.Get("LangCnToEn");
    [ObservableProperty] private string _currentSourceLang = "ZH";
    [ObservableProperty] private string _currentTargetLang = "EN";
    [ObservableProperty] private string _glossaryStatsText = Strings.Get("StatsGlossaryCount", 0);
    [ObservableProperty] private string _licenseStatusText = Strings.Get("LicenseNotActivated");
    [ObservableProperty] private bool _isLicensingEnabled;
    [ObservableProperty] private DrawingFileItem? _selectedDrawingFile;
    [ObservableProperty] private bool _hasMultipleDrawingFiles;
    [ObservableProperty] private bool _isAllDrawingsSelected = true;
    private bool _updatingDrawingSelection;

    /// <summary>任务层最近一条阶段化日志（[3/6] 总装配图.dwg 正在翻译文本 42%）。</summary>
    [ObservableProperty] private string _progressDetailText = string.Empty;

    #endregion

    public LogViewModel LogViewModel => _logViewModel ??= new LogViewModel(
        _injectedLogStore ?? App.LogStore ?? new InMemoryLogStore());

    public ObservableCollection<TextEntity> Entities { get; } = [];
    public ObservableCollection<TextEntity> FilteredEntities { get; } = [];
    public ObservableCollection<DrawingFileItem> DrawingFiles { get; } = [];
    public ObservableCollection<GlossaryEntry> GlossaryEntries { get; } = [];

    public string[] FilterOptions { get; } =
    [
        Strings.Get("FilterAll"), Strings.Get("FilterPending"), Strings.Get("FilterTranslated"),
        Strings.Get("FilterReviewed"), Strings.Get("FilterFailed"), Strings.Get("FilterGlossaryHit"),
        Strings.Get("FilterSkipped")
    ];

    /// <summary>Languages offered by the source/target pickers.</summary>
    public IReadOnlyList<TranslationLanguage> LanguageOptions => TranslationLanguages.All;

    /// <summary>
    /// Version shown in the status bar. Read from the assembly so a release cannot ship with a
    /// stale hard-coded number, with the SDK's source-revision suffix ("1.2.3+abcdef") trimmed off.
    /// </summary>
    public string AppVersionText { get; } = BuildVersionText();
    public string AppReleaseVersionText => $"版本 {TrimBuildSuffix(AppVersionText)}";
    public string AppBuildText => BuildIdentity(AppVersionText);
    public string AppBuildDateText => BuildDate(AppVersionText);
    public string AppRuntimeText => $"Windows · .NET {Environment.Version.Major}";

    private static string BuildVersionText()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "1.0.0";
        // Keep the build identity visible so release verification is unambiguous.
        return $"v{version}";
    }

    private static string TrimBuildSuffix(string version)
    {
        var value = version.TrimStart('v');
        var separator = value.IndexOf('+');
        return separator >= 0 ? value[..separator] : value;
    }

    private static string BuildIdentity(string version)
    {
        var value = version.TrimStart('v');
        var separator = value.IndexOf('+');
        if (separator < 0) return "正式构建";
        var identity = value[(separator + 1)..];
        if (identity.Length > 12 && identity.All(Uri.IsHexDigit)) return $"源代码 {identity[..8]}";
        return identity.Replace('.', '·');
    }

    private static string BuildDate(string version)
    {
        var value = version.TrimStart('v');
        var marker = value.IndexOf("ui.", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0 && marker + 18 <= value.Length &&
            DateTime.TryParseExact(value.Substring(marker + 3, 15), "yyyyMMdd-HHmmss", null,
                System.Globalization.DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd HH:mm");
        return "未提供";
    }

    /// <summary>
    /// True when the translation is being written into a CJK language. The CAD writeback uses this
    /// to choose a CJK-capable text style (instead of always assuming English output), so a
    /// Chinese drawing translated into Japanese keeps a face that can render the result.
    /// </summary>
    public bool TargetIsCjk => TranslationLanguages.IsCjk(CurrentTargetLang);

    public MainViewModel(
        IGlossaryService glossaryService,
        IExcelService excelService,
        IDwgReaderService dwgReaderService,
        IDwgWriterService dwgWriterService,
        ILicenseService licenseService,
        IAutoCadInteropService autoCadInteropService,
        ITranslationConsistencyService consistencyService,
        ITaskManager taskManager,
        TaskManagerOptions taskOptions,
        IApiClient apiClient,
        IDxfReaderService? dxfReaderService = null,
        ILogStore? logStore = null)
    {
        _config = new AppConfig();
        _glossaryService = glossaryService;
        _excelService = excelService;
        _dwgReaderService = dwgReaderService;
        _dwgWriterService = dwgWriterService;
        _licenseService = licenseService;
        _autoCadInteropService = autoCadInteropService;
        _consistencyService = consistencyService;
        _taskManager = taskManager;
        _taskOptions = taskOptions;
        _apiClient = apiClient;
        _dxfReader = dxfReaderService;
        _injectedLogStore = logStore;

        // 任务层是主链路的执行者：界面只订阅它的事件，不再自己跑解析 / 翻译 / 写回。
        SubscribeTaskEvents();

        // DrawingFiles is the shared workspace source for both the translation page and the batch page.
        // Subscribe before any restore/import can populate it so selection, summaries and row state never
        // depend on whether the user happened to open the batch page first.
        DrawingFiles.CollectionChanged += DrawingFiles_CollectionChanged;

        LoadConfig();
        if (_apiClient is IAuthenticationFailureNotifier authenticationNotifier)
            authenticationNotifier.AuthenticationRejected += OnAuthenticationRejected;
        RefreshLicenseStatus();
    }

    /// <summary>
    /// 启动初始化。每一步都在各自阶段里降级：单步失败不再吞掉整段初始化（界面照常可用），
    /// 但失败必须留下用户看得见的痕迹——状态栏 + Toast，而不是只写进日志文件。
    /// </summary>
    public async Task InitializeAsync()
    {
        // settings.json 读失败发生在 DI 阶段（那时没有 ViewModel），App 把结论攒在静态字段里带过来。
        var degraded = new List<string>();
        if (!string.IsNullOrEmpty(App.SettingsReadWarning)) degraded.Add(App.SettingsReadWarning!);

        await RunStartupStageAsync("术语库", degraded, RefreshGlossaryDataAsync);
        await RunStartupStageAsync("翻译项目", degraded, () => { RefreshTranslationProjects(); return Task.CompletedTask; });
        StatusMessage = Strings.Get("StatusReady");

        // 上次运行没跑完的任务：问用户是否继续（选"否"则清掉记录）。
        await RunStartupStageAsync("上次校对记录", degraded, RestoreSavedProofreadingAsync);
        await RunStartupStageAsync("未完成任务", degraded, () => { ResumePendingTasks(); return Task.CompletedTask; });
        await RunStartupStageAsync("上次工作区", degraded, () => { RestoreLastWorkspace(); return Task.CompletedTask; });

        ReportStartupDegradation(degraded);

        // 账户同步不再是 fire-and-forget：这一步失败只影响账户区，但异常必须有出口。
        await SafeRefreshAccountAsync(userInitiated: false); // 自动同步：启动时的后台刷新，不弹成功 toast（§A3 补完 t7）
    }

    /// <summary>
    /// 启动初始化的一个阶段。失败只降级、不中断后续阶段，并把阶段名交给调用方汇总成一句
    /// 用户可见的提示——旧实现用整段 try/catch 只写日志，用户看到"就绪"，功能却是空的。
    /// </summary>
    private async Task RunStartupStageAsync(string stage, List<string> degraded, Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex)
        {
            Log.Error(ex, "启动阶段失败：{Stage}", stage);
            degraded.Add($"{stage}（{ex.GetType().Name}）");
        }
    }

    /// <summary>
    /// 启动降级报告：没有失败就什么都不做（不打扰用户）；有失败时状态栏保留一行结论，
    /// 并用 Toast 提醒一次，诊断细节仍在日志里。
    /// </summary>
    private void ReportStartupDegradation(IReadOnlyList<string> degraded)
    {
        if (degraded.Count == 0) return;
        var message = $"启动时有 {degraded.Count} 项未完成：{string.Join("；", degraded.Take(3))}"
            + (degraded.Count > 3 ? $" 等 {degraded.Count} 项" : string.Empty)
            + "。其余功能可用，详情见日志。";
        StatusMessage = message;
        DwgTranslator.App.Services.ToastService.Warning(message);
        Log.Warning("启动降级：{Items}", string.Join(" | ", degraded));
    }

    private void RefreshLicenseStatus()
    {
        IsLicensingEnabled = _config.LicensingEnabled;
        LicenseStatusText = IsLicensingEnabled
            ? _licenseService.CurrentLicense.GetDisplayStatus()
            : "免授权版本";
    }

    public void Dispose()
    {
        if (_apiClient is IAuthenticationFailureNotifier authenticationNotifier)
            authenticationNotifier.AuthenticationRejected -= OnAuthenticationRejected;
        StopUpdateChecks();
        StopRejectedCredentialCleanup();
        StopTermSearchTimer();
        _projectAutosaveCts?.Cancel();
        _projectAutosaveCts?.Dispose();
        _projectAutosaveCts = null;
        // 关窗时把最后一次工作区状态落盘：延迟保存可能还停在 1.2s 的防抖里，不能指望它。
        SaveWorkspaceSession();
        _workspaceSessionSaveCts?.Cancel();
        _workspaceSessionSaveCts?.Dispose();
        _workspaceSessionSaveCts = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _logViewModel?.Dispose();
        _consistencyService?.FlushCache();

        // 关闭窗口时别把写回留在半途：取消当前队列，任务层的记录（已完成的部分）保持可续跑。
        try { _taskManager.CancelCurrentRun(); }
        catch (Exception ex) { Log.Debug(ex, "取消任务队列时忽略异常"); }

        // The task manager outlives this ViewModel, so detach before dropping the references.
        try { UnsubscribeTaskEvents(); }
        catch (Exception ex) { Log.Warning(ex, "退订任务层事件失败"); }

        DrawingFiles.CollectionChanged -= DrawingFiles_CollectionChanged;
        DetachDrawingFileObservers();
        _cts = null;
        _exportCts = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 主窗口关闭（应用退出前）的清理：只解除窗口级订阅与挂起的会话保存，不销毁单例 VM 本身。
    /// CTS 取消、任务队列取消等"终结性"清理仍留在 <see cref="Dispose"/>，由容器退出时触发。
    /// </summary>
    public void DetachWindowScopedState()
    {
        _projectAutosaveCts?.Cancel();
        _workspaceSessionSaveCts?.Cancel();
        DetachDrawingFileObservers();
    }
}






