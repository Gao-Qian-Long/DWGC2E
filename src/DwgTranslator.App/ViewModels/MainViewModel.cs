using CommunityToolkit.Mvvm.ComponentModel;
using DwgTranslator.Core.Logging;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Net.Http;
using Serilog;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// Main ViewModel for DWG Translator — core skeleton.
/// Partial classes: Config, Translation, ImportExport, Operations, Settings.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGlossaryService _glossaryService;
    private readonly IExcelService _excelService;
    private readonly IDwgReaderService _dwgReaderService;
    private readonly IDwgWriterService _dwgWriterService;
    private readonly ILicenseService _licenseService;
    private readonly IAutoCadInteropService _autoCadInteropService;
    private readonly IFormatCodeParser _formatCodeParser;
    private TranslationConsistencyService _consistencyService;
    private HttpClient? _httpClient;
    private DeepSeekClient? _deepSeekClient;
    private AppConfig _config;
    private string? _lastSourceFilePath;
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
    [ObservableProperty] private bool _isCnToEn = true;
    [ObservableProperty] private string _languageDirection = Strings.Get("LangCnToEn");
    [ObservableProperty] private string _currentSourceLang = "ZH";
    [ObservableProperty] private string _currentTargetLang = "EN";
    [ObservableProperty] private string _glossaryStatsText = Strings.Get("StatsGlossaryCount", 0);
    [ObservableProperty] private string _licenseStatusText = Strings.Get("LicenseNotActivated");

    #endregion

    public LogViewModel LogViewModel => _logViewModel ??= new LogViewModel(
        App.Services?.GetService<ILogStore>() ?? App.LogStore ?? new InMemoryLogStore());

    public ObservableCollection<TextEntity> Entities { get; } = new();
    public ObservableCollection<TextEntity> FilteredEntities { get; } = new();
    public ObservableCollection<GlossaryEntry> GlossaryEntries { get; } = new();

    public string[] FilterOptions { get; } = {
        Strings.Get("FilterAll"), Strings.Get("FilterPending"), Strings.Get("FilterTranslated"),
        Strings.Get("FilterReviewed"), Strings.Get("FilterFailed"), Strings.Get("FilterGlossaryHit"),
        Strings.Get("FilterSkipped")
    };

    public MainViewModel()
        : this(
            App.Services?.GetService<IGlossaryService>() ?? new GlossaryService(),
            App.Services?.GetService<IExcelService>() ?? new ExcelService(),
            App.Services?.GetService<IDwgReaderService>() ?? new DwgReaderService(),
            App.Services?.GetService<IDwgWriterService>() ?? new DwgWriterService(),
            App.Services?.GetService<ILicenseService>() ?? App.LicenseService,
            App.Services?.GetService<IAutoCadInteropService>() ?? new DwgTranslator.App.Services.AutoCadInteropService(),
            App.Services?.GetService<IFormatCodeParser>() ?? new FormatCodeParser())
    {
    }

    public MainViewModel(
        IGlossaryService glossaryService,
        IExcelService excelService,
        IDwgReaderService dwgReaderService,
        IDwgWriterService dwgWriterService,
        ILicenseService licenseService,
        IAutoCadInteropService autoCadInteropService,
        IFormatCodeParser formatCodeParser)
    {
        _config = new AppConfig();
        _glossaryService = glossaryService;
        _excelService = excelService;
        _dwgReaderService = dwgReaderService;
        _dwgWriterService = dwgWriterService;
        _licenseService = licenseService;
        _autoCadInteropService = autoCadInteropService;
        _formatCodeParser = formatCodeParser;
        _consistencyService = new TranslationConsistencyService();

        LoadConfig();
        RefreshLicenseStatus();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await RefreshGlossaryDataAsync();
            StatusMessage = Strings.Get("StatusReadyWithGlossary", GlossaryEntries.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Async initialization failed");
        }
    }

    private void RefreshLicenseStatus()
    {
        LicenseStatusText = _licenseService.CurrentLicense.GetDisplayStatus();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _exportCts?.Cancel();
        _logViewModel?.Dispose();
        _consistencyService?.FlushCache();
        _httpClient?.Dispose();
    }
}
