using CommunityToolkit.Mvvm.ComponentModel;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Logging;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;
using DwgTranslator.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
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
        App.Services?.GetService<ILogStore>() ?? App.LogStore ?? new InMemoryLogStore());

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

    private static string BuildVersionText()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "1.0.0";
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        return $"v{version}";
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
        IApiClient apiClient)
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

        // 任务层是主链路的执行者：界面只订阅它的事件，不再自己跑解析 / 翻译 / 写回。
        SubscribeTaskEvents();

        LoadConfig();
        RefreshLicenseStatus();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await RefreshGlossaryDataAsync();
            StatusMessage = Strings.Get("StatusReady");

            // 上次运行没跑完的任务：问用户是否继续（选"否"则清掉记录）。
            ResumePendingTasks();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Async initialization failed");
        }
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
        _cts?.Cancel();
        _cts?.Dispose();
        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _logViewModel?.Dispose();
        _consistencyService?.FlushCache();

        // 关闭窗口时别把写回留在半途：取消当前队列，任务层的记录（已完成的部分）保持可续跑。
        try { _taskManager.CancelCurrentRun(); }
        catch (Exception ex) { Log.Debug(ex, "取消任务队列时忽略异常"); }

        DrawingFiles.CollectionChanged -= DrawingFiles_CollectionChanged;
        _cts = null;
        _exportCts = null;
        GC.SuppressFinalize(this);
    }
}






