using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Logging;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Serilog;

// TODO(future): Extract DeepSeekClient/HttpClient creation into IDeepSeekClientFactory
//               to support DI and testability. Currently created inline in EnsureDeepSeekClient().
// TODO(future): Extract TranslationConsistencyService creation into DI with a factory
//               (needs cache file path from AppConfig, which is loaded at runtime).
// TODO(future): Extract TranslationService creation into DI with a factory
//               (depends on runtime config: systemPrompt, batchSize, maxRetry, maxConcurrency).

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// Main ViewModel for DWG Translator.
/// Features: import DWG -> translate (bi-directional ZH↔EN) -> edit -> export Excel / export DWG -> glossary management.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGlossaryService _glossaryService;
    private readonly IExcelService _excelService;
    private readonly IDwgReaderService _dwgReaderService;
    private readonly IDwgWriterService _dwgWriterService;
    private readonly ILicenseService _licenseService;
    private readonly IAutoCadInteropService _autoCadInteropService;
    private readonly FormatCodeParser _formatCodeParser;
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

    /// <summary>Descriptive text for the current long-running operation (e.g. "翻译中", "导出中").</summary>
    [ObservableProperty] private string _operationLabel = string.Empty;

    /// <summary>Whether the progress bar should show indeterminate animation (for operations without measurable progress).</summary>
    [ObservableProperty] private bool _isIndeterminate;

    /// <summary>Whether cancellation has been requested for the current operation.</summary>
    [ObservableProperty] private bool _isCancellationRequested;

    /// <summary>Whether current direction is Chinese→English (true) or English→Chinese (false).</summary>
    [ObservableProperty] private bool _isCnToEn = true;

    /// <summary>Language direction display text.</summary>
    [ObservableProperty] private string _languageDirection = Strings.Get("LangCnToEn");

    /// <summary>Current source language code.</summary>
    [ObservableProperty] private string _currentSourceLang = "ZH";

    /// <summary>Current target language code.</summary>
    [ObservableProperty] private string _currentTargetLang = "EN";

    /// <summary>Glossary entries for display/editing.</summary>
    [ObservableProperty] private string _glossaryStatsText = Strings.Get("StatsGlossaryCount", 0);

    /// <summary>License status display text.</summary>
    [ObservableProperty] private string _licenseStatusText = Strings.Get("LicenseNotActivated");

    #endregion

    /// <summary>
    /// ViewModel for the embedded log viewer panel.
    /// </summary>
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
            App.Services?.GetService<FormatCodeParser>() ?? new FormatCodeParser())
    {
    }

    public MainViewModel(
        IGlossaryService glossaryService,
        IExcelService excelService,
        IDwgReaderService dwgReaderService,
        IDwgWriterService dwgWriterService,
        ILicenseService licenseService,
        IAutoCadInteropService autoCadInteropService,
        FormatCodeParser formatCodeParser)
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

    /// <summary>
    /// 异步初始化：加载术语库等需要异步IO的操作。
    /// 在 MainWindow.Loaded 事件中调用，避免构造函数中同步阻塞 UI 线程。
    /// </summary>
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

    #region Config

    private void LoadConfig()
    {
        try
        {
            var appDataPath = Path.Combine(App.AppDataDir, "settings.json");
            var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            _settingsPath = File.Exists(appDataPath) ? appDataPath : bundledPath;

            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }

            // Decrypt API key if stored with DPAPI protection
            _config.DeepSeekApiKey = AppConfig.DecryptApiKey(_config.DeepSeekApiKey);

            // Resolve paths
            if (!Path.IsPathRooted(_config.ExportDirectory))
                _config.ExportDirectory = Path.Combine(App.AppDataDir, _config.ExportDirectory);
            if (!Path.IsPathRooted(_config.LogDirectory))
                _config.LogDirectory = Path.Combine(App.AppDataDir, _config.LogDirectory);
            if (!Path.IsPathRooted(_config.GlossaryPath))
            {
                var appDataGlossary = Path.Combine(App.AppDataDir, _config.GlossaryPath);
                if (File.Exists(appDataGlossary))
                    _config.GlossaryPath = appDataGlossary;
            }

            Directory.CreateDirectory(_config.ExportDirectory);

            // Load glossary
            RefreshGlossaryData();

            // Load translation consistency cache
            var cachePath = Path.Combine(App.AppDataDir, "translation_cache.json");
            _consistencyService = new TranslationConsistencyService(cachePath);

            // Apply initial language direction from config
            ApplyLanguageDirection();

            StatusMessage = Strings.Get("StatusReadyWithGlossary", GlossaryEntries.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Config load error");
            StatusMessage = Strings.Get("StatusConfigLoadFailed");
        }
    }

    private void ApplyLanguageDirection()
    {
        if (_config.SourceLanguage == "ZH" && _config.TargetLanguage == "EN")
        {
            IsCnToEn = true;
            LanguageDirection = Strings.Get("LangCnToEn");
            CurrentSourceLang = "ZH";
            CurrentTargetLang = "EN";
        }
        else
        {
            IsCnToEn = false;
            LanguageDirection = Strings.Get("LangEnToCn");
            CurrentSourceLang = "EN";
            CurrentTargetLang = "ZH";
        }
    }

    /// <summary>
    /// Synchronous refresh from in-memory glossary entries only.
    /// File loading is handled by InitializeAsync() via RefreshGlossaryDataAsync().
    /// This avoids blocking the UI thread with file I/O.
    /// </summary>
    private void RefreshGlossaryData()
    {
        GlossaryEntries.Clear();

        foreach (var entry in _glossaryService.GetAllEntries())
            GlossaryEntries.Add(entry);

        GlossaryStatsText = Strings.Get("StatsGlossaryCount", GlossaryEntries.Count);
    }

    /// <summary>
    /// 异步版本的术语库加载，用于 InitializeAsync 和切换翻译方向。
    /// 避免在 UI 线程上同步阻塞。
    /// </summary>
    private async Task RefreshGlossaryDataAsync()
    {
        GlossaryEntries.Clear();

        var glossaryFile = ResolveGlossaryPath();
        if (File.Exists(glossaryFile))
        {
            await _glossaryService.LoadGlossaryAsync(glossaryFile);
        }

        foreach (var entry in _glossaryService.GetAllEntries())
            GlossaryEntries.Add(entry);

        GlossaryStatsText = Strings.Get("StatsGlossaryCount", GlossaryEntries.Count);
    }

    private string ResolveGlossaryPath()
    {
        // Try AppData first, then bundled
        var appDataGlossary = Path.Combine(App.AppDataDir, "glossaries", "mechanical_zh_en.json");
        if (File.Exists(appDataGlossary)) return appDataGlossary;

        var bundledGlossary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glossaries", "mechanical_zh_en.json");
        if (File.Exists(bundledGlossary)) return bundledGlossary;

        return _config.GlossaryPath;
    }

    #endregion

    #region Language Direction Toggle

    /// <summary>
    /// Toggle window always-on-top.
    /// </summary>
    [RelayCommand]
    private void ToggleTopmost()
    {
        if (Application.Current.MainWindow != null)
        {
            Application.Current.MainWindow.Topmost = !Application.Current.MainWindow.Topmost;
            IsTopmost = Application.Current.MainWindow.Topmost;
        }
        StatusMessage = IsTopmost ? Strings.Get("StatusTopmostOn") : Strings.Get("StatusTopmostOff");
    }

    /// <summary>
    /// Toggle translation direction between CN→EN and EN→CN.
    /// </summary>
    [RelayCommand]
    private async Task ToggleLanguageDirectionAsync()
    {
        if (IsProcessing) return;

        // Confirm before switching if there are translated items that will be reset
        var translatedCount = Entities.Count(e =>
            e.Status == TranslationStatus.Translated ||
            e.Status == TranslationStatus.Reviewed ||
            e.Status == TranslationStatus.WritebackSuccess);
        if (translatedCount > 0)
        {
            var confirmResult = MessageBox.Show(
                Strings.Get("MsgDirectionSwitchConfirm", translatedCount),
                Strings.Get("MsgTitleConfirm"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmResult != MessageBoxResult.Yes) return;
        }

        IsCnToEn = !IsCnToEn;

        if (IsCnToEn)
        {
            LanguageDirection = Strings.Get("LangCnToEn");
            CurrentSourceLang = "ZH";
            CurrentTargetLang = "EN";
            _config.SourceLanguage = "ZH";
            _config.TargetLanguage = "EN";
        }
        else
        {
            LanguageDirection = Strings.Get("LangEnToCn");
            CurrentSourceLang = "EN";
            CurrentTargetLang = "ZH";
            _config.SourceLanguage = "EN";
            _config.TargetLanguage = "ZH";
        }

        // Reload glossary with correct direction (async, 不阻塞 UI)
        await RefreshGlossaryDataAsync();

        // Reset existing translations since direction changed
        foreach (var entity in Entities)
        {
            if (entity.Status == TranslationStatus.Translated ||
                entity.Status == TranslationStatus.Reviewed ||
                entity.Status == TranslationStatus.WritebackSuccess)
            {
                entity.Status = TranslationStatus.Pending;
                entity.TranslatedText = string.Empty;
                entity.GlossaryHit = false;
            }
        }

        ApplyFilter();
        UpdateStatistics();
        StatusMessage = Strings.Get("StatusDirectionSwitched", LanguageDirection);
    }

    #endregion

    #region Glossary Management

    /// <summary>
    /// Open glossary management dialog.
    /// </summary>
    [RelayCommand]
    private void OpenGlossaryManager()
    {
        var titleSuffix = $"{LanguageDirection} ({GlossaryEntries.Count})";
        var dialog = new Views.GlossaryManagerDialog(_glossaryService, GlossaryEntries, titleSuffix)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.SavedEntries != null)
        {
            RefreshGlossaryDataFromList(dialog.SavedEntries);
            StatusMessage = Strings.Get("StatusGlossarySaved", dialog.SavedEntries.Count);
        }
    }

    private void RefreshGlossaryDataFromList(List<GlossaryEntry> entries)
    {
        GlossaryEntries.Clear();
        foreach (var entry in entries)
            GlossaryEntries.Add(entry);
        GlossaryStatsText = Strings.Get("StatsGlossaryCount", GlossaryEntries.Count);
    }

    /// <summary>
    /// Import glossary entries from Excel or JSON.
    /// </summary>
    [RelayCommand]
    private async Task ImportGlossaryAsync()
    {
        if (IsProcessing) return;

        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("FilterGlossaryFiles"),
            Title = Strings.Get("DialogTitleImportGlossary")
        };
        if (dialog.ShowDialog() != true) return;

        var targetPath = Path.Combine(App.AppDataDir, "glossaries", "mechanical_zh_en.json");
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(dialog.FileName, targetPath, overwrite: true);
        }

        await _glossaryService.LoadGlossaryAsync(targetPath);
        RefreshGlossaryData();
        StatusMessage = Strings.Get("StatusGlossaryImported", GlossaryEntries.Count);
    }

    #endregion

    #region DWG/DXF Import

    [RelayCommand]
    private async Task ImportDwgAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("FilterCadFiles"),
            Title = Strings.Get("DialogTitleSelectDwg"),
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        var filePaths = dialog.FileNames;
        if (filePaths.Length == 0) return;

        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = Strings.Get("OperationImporting");
        StatusMessage = Strings.Get("StatusReadingCad", filePaths.Length);

        try
        {
            var importErrors = new List<string>();
            var allEntities = await Task.Run(() =>
            {
                var result = new List<TextEntity>();
                foreach (var path in filePaths)
                {
                    try
                    {
                        // Use appropriate reader based on file extension
                        var extension = Path.GetExtension(path).ToLowerInvariant();
                        List<TextEntity> entities;
                        if (extension == ".dxf")
                        {
                            var dxfReader = App.Services?.GetService<IDxfReaderService>();
                            entities = dxfReader?.ExtractFromFile(path) ?? new List<TextEntity>();
                        }
                        else
                        {
                            entities = _dwgReaderService.ExtractFromFile(path);
                        }
                        foreach (var e in entities)
                            e.Notes = $"Source: {Path.GetFileNameWithoutExtension(path)}";
                        result.AddRange(entities);
                    }
                    catch (Exception ex)
                    {
                        importErrors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"Failed to read {path}: {ex.Message}");
                    }
                }
                return result;
            });

            _lastSourceFilePath = filePaths[0];
            Entities.Clear();
            foreach (var entity in allEntities)
                Entities.Add(entity);

            ApplyFilter();
            UpdateStatistics();

            if (importErrors.Count > 0 && allEntities.Count > 0)
            {
                StatusMessage = Strings.Get("StatusImportPartialFail", allEntities.Count, importErrors.Count);
                Log.Warning("Import errors ({Count}): {Errors}", importErrors.Count, string.Join("; ", importErrors));
            }
            else if (importErrors.Count > 0 && allEntities.Count == 0)
            {
                StatusMessage = Strings.Get("StatusCadImportFailed");
                MessageBox.Show(Strings.Get("MsgCadImportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                StatusMessage = Strings.Get("StatusImported", allEntities.Count, filePaths.Length);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CAD import failed");
            StatusMessage = Strings.Get("StatusCadImportFailed");
            MessageBox.Show(Strings.Get("MsgCadImportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            IsIndeterminate = false;
            OperationLabel = string.Empty;
        }
    }

    [RelayCommand]
    private async Task TranslateAsync()
    {
        if (IsProcessing) return;

        if (!_licenseService.CanExecuteOperation())
        {
            StatusMessage = Strings.Get("StatusLicenseRequired");
            PromptForActivation();
            return;
        }

        var entitiesToTranslate = Entities
            .Where(e => !e.IsXref && e.Status == TranslationStatus.Pending)
            .ToList();

        if (entitiesToTranslate.Count == 0)
        {
            StatusMessage = Strings.Get("StatusNoPendingText");
            return;
        }

        if (string.IsNullOrEmpty(_config.DeepSeekApiKey))
        {
            StatusMessage = Strings.Get("StatusConfigureApiKey");
            MessageBox.Show(Strings.Get("MsgNoApiKey"), Strings.Get("MsgTitleConfigError"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsProcessing = true;
        OperationLabel = Strings.Get("OperationTranslating");
        IsCancellationRequested = false;
        _cts = new CancellationTokenSource();
        TranslatedCount = 0;
        FailedCount = 0;
        CacheHitCount = 0;
        ProgressValue = 0;
        var totalCount = entitiesToTranslate.Count;
        var completedCount = 0;

        StatusMessage = Strings.Get("StatusTranslating", 0, totalCount);

        try
        {
            var systemPrompt = LoadSystemPrompt();
            EnsureDeepSeekClient();

            // Use Progress<T> to receive each translation result as it completes
            var progress = new Progress<TranslationPair>(pair =>
            {
                // Marshal to UI thread via BeginInvoke (non-blocking) to avoid UI freeze
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var entity = Entities.FirstOrDefault(e => e.Handle == pair.Handle);
                    if (entity == null)
                    {
                        // New entity from a DWG we just imported? Look through all.
                        entity = Entities.FirstOrDefault(e =>
                            string.Equals(e.Handle, pair.Handle, StringComparison.OrdinalIgnoreCase));
                    }
                    if (entity == null) return;

                    entity.TranslatedText = pair.TranslatedText;
                    entity.GlossaryHit = pair.GlossaryHit;
                    entity.Status = pair.Status;

                    Interlocked.Increment(ref completedCount);

                    if (pair.Status == TranslationStatus.Translated)
                    {
                        TranslatedCount++;
                        if (pair.GlossaryHit) GlossaryHitCount++;
                    }
                    else if (pair.Status == TranslationStatus.TranslationFailed)
                    {
                        FailedCount++;
                    }

                    CacheHitCount = _consistencyService.CacheSize;
                    ProgressValue = (double)completedCount / totalCount * 100;
                    StatusMessage = Strings.Get("StatusTranslating", completedCount, totalCount);
                });
            });

            // Run translation service on a background thread to keep UI responsive
            await Task.Run(async () =>
            {
                var translationService = new TranslationService(
                    _glossaryService, _formatCodeParser, _deepSeekClient!, systemPrompt,
                    _config.BatchSize, _config.MaxRetryCount, _consistencyService, maxConcurrency: 5);

                await translationService.TranslateBatchWithProgressAsync(
                    entitiesToTranslate, CurrentSourceLang, CurrentTargetLang, progress, _cts.Token);
            }, _cts.Token);

            ApplyFilter();
            UpdateStatistics();
            StatusMessage = Strings.Get("StatusTranslateComplete", TranslatedCount, FailedCount, LanguageDirection);
        }
        catch (TaskCanceledException)
        {
            StatusMessage = Strings.Get("StatusTranslateTimeout");
            MessageBox.Show(Strings.Get("MsgTranslateTimeout"), Strings.Get("MsgTitleTimeout"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (HttpRequestException ex)
        {
            // Log full details, show sanitized message to user
            Log.Error(ex, "Translation API error");
            StatusMessage = Strings.Get("StatusApiFailed");
            MessageBox.Show(Strings.Get("MsgApiError"), Strings.Get("MsgTitleApiError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.Get("StatusTranslateCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Translation error");
            StatusMessage = Strings.Get("StatusTranslateFailed");
            MessageBox.Show(Strings.Get("MsgTranslateError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsProcessing = false;
            IsIndeterminate = false;
            OperationLabel = string.Empty;
            IsCancellationRequested = false;

            // BUG FIX: 使用 ContextIdle 优先级强制更新最终状态，
            // 确保在所有 Progress<T> 的 BeginInvoke 回调执行完毕后再设置状态消息，
            // 防止残留的异步回调覆盖"翻译完成"状态。
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                StatusMessage = Strings.Get("StatusTranslateComplete", TranslatedCount, FailedCount, LanguageDirection);
                UpdateStatistics();
                ApplyFilter();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    [RelayCommand]
    private void CancelTranslate()
    {
        IsCancellationRequested = true;
        _cts?.Cancel();
        StatusMessage = Strings.Get("StatusStoppingTranslation");
    }

    [RelayCommand]
    private void CancelExport()
    {
        IsCancellationRequested = true;
        _exportCts?.Cancel();
        StatusMessage = Strings.Get("StatusCancellingExport");
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        if (IsProcessing) return;

        var failedEntities = Entities.Where(e => e.Status == TranslationStatus.TranslationFailed).ToList();
        if (failedEntities.Count == 0) { StatusMessage = Strings.Get("StatusNoFailedRetry"); return; }

        foreach (var e in failedEntities) { e.Status = TranslationStatus.Pending; e.TranslatedText = string.Empty; }
        UpdateStatistics();
        await TranslateAsync();
    }

    #endregion

    #region Excel I/O

    [RelayCommand]
    private async Task ExportExcelAsync()
    {
        if (IsProcessing) return;
        if (Entities.Count == 0) { StatusMessage = Strings.Get("StatusNoExportData"); return; }

        var dialog = new SaveFileDialog
        {
            Filter = Strings.Get("FilterExcelFiles"),
            Title = Strings.Get("DialogTitleSaveExcel"),
            FileName = $"translations_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };
        if (dialog.ShowDialog() != true) return;

        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = Strings.Get("OperationExportExcel");
        StatusMessage = Strings.Get("StatusExportingExcel");
        try
        {
            await _excelService.ExportToExcelAsync(Entities.ToList(), dialog.FileName);
            StatusMessage = Strings.Get("StatusExcelExported", Entities.Count, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel export failed");
            StatusMessage = Strings.Get("StatusExcelExportFailed");
            MessageBox.Show(Strings.Get("ExcelExportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsProcessing = false; IsIndeterminate = false; OperationLabel = string.Empty; }
    }

    [RelayCommand]
    private async Task ImportExcelAsync()
    {
        if (IsProcessing) return;

        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("FilterExcelAllFiles"),
            Title = Strings.Get("DialogTitleSelectExcel")
        };
        if (dialog.ShowDialog() != true) return;

        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = Strings.Get("OperationImportExcel");
        StatusMessage = Strings.Get("StatusImportingExcel");
        try
        {
            var importedEntities = await _excelService.ImportFromExcelAsync(dialog.FileName);
            int updatedCount = 0;
            foreach (var imported in importedEntities)
            {
                var existing = Entities.FirstOrDefault(e => e.Handle == imported.Handle);
                if (existing != null)
                {
                    existing.TranslatedText = imported.TranslatedText;
                    existing.GlossaryHit = imported.GlossaryHit;
                    existing.Status = imported.Status;
                    existing.Notes = imported.Notes;
                    updatedCount++;
                }
            }
            ApplyFilter();
            UpdateStatistics();
            StatusMessage = Strings.Get("StatusExcelImported", updatedCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel import failed");
            StatusMessage = Strings.Get("StatusExcelImportFailed");
            MessageBox.Show(Strings.Get("ExcelImportFormatError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsProcessing = false; IsIndeterminate = false; OperationLabel = string.Empty; }
    }

    #endregion

    #region DWG/DXF Export (核心功能)

    /// <summary>
    /// Export translated DWG/DXF file.
    /// If AutoCAD is available, offers high-precision writeback via COM.
    /// Otherwise falls back to ACadSharp offline writeback.
    /// </summary>
    [RelayCommand]
    private async Task ExportDwgAsync()
    {
        if (IsProcessing) return;

        if (!_licenseService.CanExecuteOperation())
        {
            StatusMessage = Strings.Get("StatusLicenseRequired");
            PromptForActivation();
            return;
        }

        var entitiesToWrite = Entities
            .Where(e => e.Status == TranslationStatus.Translated || e.Status == TranslationStatus.Reviewed)
            .ToList();

        if (entitiesToWrite.Count == 0)
        {
            StatusMessage = Strings.Get("StatusNoTranslatedText");
            MessageBox.Show(Strings.Get("MsgNoTranslatedData"),
                Strings.Get("MsgTitleNoData"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? sourceFilePath = null;
        if (!string.IsNullOrEmpty(_lastSourceFilePath) && File.Exists(_lastSourceFilePath))
            sourceFilePath = _lastSourceFilePath;
        else
        {
            var dialog = new OpenFileDialog
            {
                Filter = Strings.Get("FilterCadFiles"),
                Title = Strings.Get("DialogTitleSelectCadFile")
            };
            if (dialog.ShowDialog() == true) sourceFilePath = dialog.FileName;
        }

        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
        { StatusMessage = Strings.Get("StatusSelectCadFile"); return; }

        // Determine output format based on source file
        var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        var isDxfSource = sourceExtension == ".dxf";

        var dialog2 = new SaveFileDialog
        {
            Filter = isDxfSource ? Strings.Get("FilterDxfFiles") : Strings.Get("FilterDwgFiles"),
            Title = Strings.Get("DialogTitleSaveDwg", isDxfSource ? "DXF" : "DWG"),
            FileName = Path.GetFileNameWithoutExtension(sourceFilePath) + "_translated" + sourceExtension
        };
        if (dialog2.ShowDialog() != true) return;

        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = Strings.Get("OperationExporting");
        IsCancellationRequested = false;
        _exportCts = new CancellationTokenSource();
        StatusMessage = Strings.Get("StatusExportingDwg");
        ProgressValue = 0;

        try
        {
            Core.Services.DwgWriteResult result;
            bool usedAcadInterop = false;

            // Show export mode selection dialog
            var modeDialog = new Views.ExportModeDialog(_autoCadInteropService.IsAutoCADAvailable(_config))
            {
                Owner = Application.Current.MainWindow
            };

            if (modeDialog.ShowDialog() != true)
            {
                StatusMessage = Strings.Get("StatusExportCancelled");
                return;
            }

            if (modeDialog.SelectedMode == Views.ExportModeDialog.ExportMode.AutoCAD)
            {
                usedAcadInterop = true;

                // Pre-alert user about security dialog
                MessageBox.Show(
                    Strings.Get("MsgAutoCadSecurity"),
                    Strings.Get("MsgTitleAutoCadSecurity"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                var writebackProgress = new Progress<string>(msg => StatusMessage = msg);
                result = await Task.Run(async () =>
                    await _autoCadInteropService.WritebackViaAutoCadAsync(sourceFilePath, dialog2.FileName, entitiesToWrite, IsCnToEn, _config, writebackProgress));
            }
            else
            {
                result = await Task.Run(() =>
                    _dwgWriterService.WriteTranslations(sourceFilePath, dialog2.FileName, entitiesToWrite, IsCnToEn));
            }

            ProgressValue = 100;

            if (result.SuccessCount > 0)
            {
                // Consume license use for successful export
                ConsumeLicenseForExport();

                string modeText = usedAcadInterop ? Strings.Get("ExportModeAutoCad") : Strings.Get("ExportModeOffline");
                string formatText = isDxfSource ? "DXF" : "DWG";
                StatusMessage = Strings.Get("StatusDwgExportComplete", formatText, modeText, result.SuccessCount, Path.GetFileName(dialog2.FileName));
                MessageBox.Show(
                    Strings.Get("MsgDwgExportSuccess", formatText, modeText, result.SuccessCount, result.FailCount, dialog2.FileName),
                    Strings.Get("MsgTitleExportSuccess"),
                    MessageBoxButton.OK,
                    result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            else
            {
                var errorMsg = string.Join("\n", result.Errors.Take(5));
                string formatText = isDxfSource ? "DXF" : "DWG";
                StatusMessage = Strings.Get("StatusDwgExportFailed", formatText);
                MessageBox.Show(Strings.Get("MsgDwgExportError", formatText, errorMsg), Strings.Get("MsgTitleExportError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.Get("StatusExportCancelled2");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CAD export failed");
            StatusMessage = Strings.Get("StatusCadExportFailed");
            MessageBox.Show(Strings.Get("MsgCadExportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            IsIndeterminate = false;
            OperationLabel = string.Empty;
            IsCancellationRequested = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    // NOTE: AutoCAD COM interop logic has been extracted to IAutoCadInteropService / AutoCadInteropService.
    // See: src/DwgTranslator.App/Services/AutoCadInteropService.cs

    #endregion

    #region Operations

    [RelayCommand]
    private void MarkAllReviewed()
    {
        if (IsProcessing) return;

        int count = 0;
        foreach (var entity in Entities.Where(e => e.Status == TranslationStatus.Translated))
        { entity.Status = TranslationStatus.Reviewed; count++; }
        UpdateStatistics();
        ApplyFilter();
        StatusMessage = Strings.Get("StatusReviewed", count);
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (IsProcessing) return;
        if (Entities.Count == 0) return;

        var result = MessageBox.Show(
            Strings.Get("MsgClearConfirmBody", Entities.Count),
            Strings.Get("MsgClearConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        Entities.Clear();
        FilteredEntities.Clear();
        TotalCount = 0; TranslatedCount = 0; FailedCount = 0;
        GlossaryHitCount = 0; CacheHitCount = 0; VisibleCount = 0;
        ProgressValue = 0; _lastSourceFilePath = null;
        StatusMessage = Strings.Get("StatusCleared");
    }

    /// <summary>
    /// Auto-triggered when FilterStatusText changes via ComboBox binding.
    /// </summary>
    partial void OnFilterStatusTextChanged(string value)
    {
        ApplyFilter();
    }

    [RelayCommand]
    private void FilterByStatus(string? status)
    {
        FilterStatusText = status ?? Strings.Get("FilterAll");
        // ApplyFilter is already called by OnFilterStatusTextChanged
    }

    [RelayCommand]
    private void ApplySearch() => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<TextEntity> filtered = Entities;
        var filterAll = Strings.Get("FilterAll");
        if (FilterStatusText != filterAll)
        {
            var filterPending = Strings.Get("FilterPending");
            var filterTranslated = Strings.Get("FilterTranslated");
            var filterReviewed = Strings.Get("FilterReviewed");
            var filterFailed = Strings.Get("FilterFailed");
            var filterGlossaryHit = Strings.Get("FilterGlossaryHit");
            var filterSkipped = Strings.Get("FilterSkipped");

            if (FilterStatusText == filterPending)
                filtered = filtered.Where(e => e.Status == TranslationStatus.Pending);
            else if (FilterStatusText == filterTranslated)
                filtered = filtered.Where(e => e.Status == TranslationStatus.Translated);
            else if (FilterStatusText == filterReviewed)
                filtered = filtered.Where(e => e.Status == TranslationStatus.Reviewed || e.Status == TranslationStatus.WritebackSuccess);
            else if (FilterStatusText == filterFailed)
                filtered = filtered.Where(e => e.Status == TranslationStatus.TranslationFailed);
            else if (FilterStatusText == filterGlossaryHit)
                filtered = filtered.Where(e => e.GlossaryHit);
            else if (FilterStatusText == filterSkipped)
                filtered = filtered.Where(e => e.Status == TranslationStatus.Skipped);
        }
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            filtered = filtered.Where(e =>
                (e.PlainText ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.TranslatedText ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.EntityType ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        FilteredEntities.Clear();
        foreach (var entity in filtered) FilteredEntities.Add(entity);
        VisibleCount = FilteredEntities.Count;
    }

    private void UpdateStatistics()
    {
        TotalCount = Entities.Count;
        TranslatedCount = Entities.Count(e => e.Status == TranslationStatus.Translated ||
                                                e.Status == TranslationStatus.Reviewed ||
                                                e.Status == TranslationStatus.WritebackSuccess);
        FailedCount = Entities.Count(e => e.Status == TranslationStatus.TranslationFailed);
        GlossaryHitCount = Entities.Count(e => e.GlossaryHit);
        CacheHitCount = _consistencyService.CacheSize;
        VisibleCount = FilteredEntities.Count;
    }

    #endregion

    #region Settings

    /// <summary>
    /// Toggle the log viewer panel visibility.
    /// </summary>
    [RelayCommand]
    private void ToggleLogViewer()
    {
        LogViewModel.ToggleVisibilityCommand.Execute(null);
    }

    [RelayCommand]
    private void Settings()
    {
        var dialog = new Views.SettingsDialog
        {
            Owner = Application.Current.MainWindow
        };
        var result = dialog.ShowDialog();

        // Only reload config and reset HTTP client if settings were actually saved
        if (result == true)
        {
            // Save current config snapshot for comparison
            var oldApiKey = _config.DeepSeekApiKey;
            var oldBaseUrl = _config.DeepSeekBaseUrl;
            var oldModel = _config.DeepSeekModel;

            LoadConfig();

            // Only reset HTTP client if API settings actually changed
            if (_config.DeepSeekApiKey != oldApiKey ||
                _config.DeepSeekBaseUrl != oldBaseUrl ||
                _config.DeepSeekModel != oldModel)
            {
                _httpClient?.Dispose();
                _httpClient = null;
                _deepSeekClient = null;
                StatusMessage = Strings.Get("StatusApiReset");
            }
            else
            {
                StatusMessage = Strings.Get("StatusSettingsSaved");
            }
        }
    }

    #endregion

    #region Help

    [RelayCommand]
    private void ShowHelp()
    {
        var dialog = new Views.HelpDialog
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    #endregion

    #region License

    [RelayCommand]
    private void OpenLicenseDialog()
    {
        var dialog = new Views.LicenseDialog(_licenseService)
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
        RefreshLicenseStatus();
    }

    private void PromptForActivation()
    {
        var result = MessageBox.Show(
            Strings.Get("MsgActivationPrompt"),
            Strings.Get("MsgTitleActivation"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            OpenLicenseDialog();
        }
    }

    private bool ConsumeLicenseForExport()
    {
        if (!_licenseService.CanExecuteOperation())
        {
            PromptForActivation();
            return false;
        }

        // Trial licenses consume one use on successful DWG export
        if (_licenseService.CurrentLicense.Type == LicenseType.Trial)
        {
            _licenseService.ConsumeTrialUse();
            RefreshLicenseStatus();
        }
        return true;
    }

    #endregion

    #region Internal Helpers

    private string LoadSystemPrompt()
    {
        var appDataPath = Path.Combine(App.AppDataDir, "prompts", "deepl_context.txt");
        var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "deepl_context.txt");
        var promptPath = File.Exists(appDataPath) ? appDataPath : bundledPath;
        if (File.Exists(promptPath)) return File.ReadAllText(promptPath);
        return "You are a professional engineering translator. Output ONLY the translated text, nothing else.";
    }

    private void EnsureDeepSeekClient()
    {
        if (_deepSeekClient != null) return;

        if (string.IsNullOrWhiteSpace(_config.DeepSeekBaseUrl))
            throw new InvalidOperationException(Strings.Get("SettingsTestEnterKey"));

        if (string.IsNullOrWhiteSpace(_config.DeepSeekApiKey))
            throw new InvalidOperationException(Strings.Get("SettingsTestEnterKey"));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.DeepSeekBaseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.DeepSeekApiKey}");
        _deepSeekClient = new DeepSeekClient(_httpClient, _config.DeepSeekModel);
    }

    public void Dispose()
    {
        // Cancel any active operations before cleanup
        _cts?.Cancel();
        _exportCts?.Cancel();

        _logViewModel?.Dispose();
        _consistencyService?.FlushCache();
        _httpClient?.Dispose();
    }

    #endregion
}