using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Serilog;

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
    private readonly FormatCodeParser _formatCodeParser;
    private TranslationConsistencyService _consistencyService;
    private HttpClient? _httpClient;
    private DeepSeekClient? _deepSeekClient;
    private AppConfig _config;
    private string? _lastSourceFilePath;
    private string? _settingsPath;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _exportCts;

    #region Bindable Properties

    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _selectedFilePath = string.Empty;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _translatedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _glossaryHitCount;
    [ObservableProperty] private int _cacheHitCount;
    [ObservableProperty] private string _filterStatusText = "全部";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private bool _isTopmost;

    /// <summary>Whether current direction is Chinese→English (true) or English→Chinese (false).</summary>
    [ObservableProperty] private bool _isCnToEn = true;

    /// <summary>Language direction display text.</summary>
    [ObservableProperty] private string _languageDirection = "中文 → 英文";

    /// <summary>Current source language code.</summary>
    [ObservableProperty] private string _currentSourceLang = "ZH";

    /// <summary>Current target language code.</summary>
    [ObservableProperty] private string _currentTargetLang = "EN";

    /// <summary>Glossary entries for display/editing.</summary>
    [ObservableProperty] private string _glossaryStatsText = "术语库: 0 条";

    /// <summary>License status display text.</summary>
    [ObservableProperty] private string _licenseStatusText = "未激活";

    #endregion

    public ObservableCollection<TextEntity> Entities { get; } = new();
    public ObservableCollection<TextEntity> FilteredEntities { get; } = new();
    public ObservableCollection<GlossaryEntry> GlossaryEntries { get; } = new();

    public string[] FilterOptions { get; } = { "全部", "待翻译", "已翻译", "审阅完成", "翻译失败", "术语命中", "已跳过" };

    public MainViewModel()
    {
        _config = new AppConfig();
        _glossaryService = new GlossaryService();
        _excelService = new ExcelService();
        _dwgReaderService = new DwgReaderService();
        _dwgWriterService = new DwgWriterService();
        _licenseService = App.LicenseService;
        _formatCodeParser = new FormatCodeParser();
        _consistencyService = new TranslationConsistencyService();

        LoadConfig();
        RefreshLicenseStatus();
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

            StatusMessage = $"就绪 — 术语库: {GlossaryEntries.Count} 条";
        }
        catch (Exception ex)
        {
            StatusMessage = $"配置加载错误: {ex.Message}";
        }
    }

    private void ApplyLanguageDirection()
    {
        if (_config.SourceLanguage == "ZH" && _config.TargetLanguage == "EN")
        {
            IsCnToEn = true;
            LanguageDirection = "中文 → 英文";
            CurrentSourceLang = "ZH";
            CurrentTargetLang = "EN";
        }
        else
        {
            IsCnToEn = false;
            LanguageDirection = "英文 → 中文";
            CurrentSourceLang = "EN";
            CurrentTargetLang = "ZH";
        }
    }

    private void RefreshGlossaryData()
    {
        GlossaryEntries.Clear();

        var glossaryFile = ResolveGlossaryPath();
        if (File.Exists(glossaryFile))
        {
            _glossaryService.LoadGlossaryAsync(glossaryFile).GetAwaiter().GetResult();
        }

        foreach (var entry in _glossaryService.GetAllEntries())
            GlossaryEntries.Add(entry);

        GlossaryStatsText = $"术语库: {GlossaryEntries.Count} 条";
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
        StatusMessage = IsTopmost ? "窗口已置顶 📍" : "窗口取消置顶";
    }

    /// <summary>
    /// Toggle translation direction between CN→EN and EN→CN.
    /// </summary>
    [RelayCommand]
    private void ToggleLanguageDirection()
    {
        IsCnToEn = !IsCnToEn;

        if (IsCnToEn)
        {
            LanguageDirection = "中文 → 英文";
            CurrentSourceLang = "ZH";
            CurrentTargetLang = "EN";
            _config.SourceLanguage = "ZH";
            _config.TargetLanguage = "EN";
        }
        else
        {
            LanguageDirection = "英文 → 中文";
            CurrentSourceLang = "EN";
            CurrentTargetLang = "ZH";
            _config.SourceLanguage = "EN";
            _config.TargetLanguage = "ZH";
        }

        // Reload glossary with correct direction
        RefreshGlossaryData();

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
        StatusMessage = $"语言方向已切换为: {LanguageDirection}。请重新点击「翻译」。";
    }

    #endregion

    #region Glossary Management

    /// <summary>
    /// Open glossary management dialog.
    /// </summary>
    [RelayCommand]
    private void OpenGlossaryManager()
    {
        // Create a simple management window
        var window = new Window
        {
            Title = $"术语库管理 — {LanguageDirection} ({GlossaryEntries.Count} 条)",
            Width = 600,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.ToolWindow
        };

        var grid = new System.Windows.Controls.Grid();
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var dataGrid = new System.Windows.Controls.DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = true,
            CanUserDeleteRows = true,
            Margin = new Thickness(5, 5, 5, 5)
        };

        dataGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        { Header = "源语言", Binding = new System.Windows.Data.Binding("Source"), Width = 200 });
        dataGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        { Header = "目标语言", Binding = new System.Windows.Data.Binding("Target"), Width = 200 });
        dataGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        { Header = "分类", Binding = new System.Windows.Data.Binding("Category"), Width = 100 });

        // Clone glossary entries for editing
        var editableEntries = new ObservableCollection<GlossaryEntry>();
        foreach (var e in GlossaryEntries)
            editableEntries.Add(new GlossaryEntry { Source = e.Source, Target = e.Target, Category = e.Category });

        dataGrid.ItemsSource = editableEntries;
        System.Windows.Controls.Grid.SetRow(dataGrid, 0);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(5, 5, 5, 5)
        };

        var saveButton = new System.Windows.Controls.Button
        {
            Content = "💾 保存术语库",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 5, 0)
        };
        saveButton.Click += async (s, e) =>
        {
            // Save to AppData glossary file
            var targetPath = Path.Combine(App.AppDataDir, "glossaries", "mechanical_zh_en.json");
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var list = editableEntries.Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Target)).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(targetPath, json);

            // Reload into service
            await _glossaryService.LoadGlossaryAsync(targetPath);
            RefreshGlossaryDataFromList(list);

            window.Close();
            StatusMessage = $"术语库已保存: {list.Count} 条";
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "取消",
            Padding = new Thickness(10, 5, 10, 5)
        };
        cancelButton.Click += (s, e) => window.Close();

        buttonPanel.Children.Add(saveButton);
        buttonPanel.Children.Add(cancelButton);
        System.Windows.Controls.Grid.SetRow(buttonPanel, 1);

        grid.Children.Add(dataGrid);
        grid.Children.Add(buttonPanel);
        window.Content = grid;
        window.ShowDialog();
    }

    private void RefreshGlossaryDataFromList(List<GlossaryEntry> entries)
    {
        GlossaryEntries.Clear();
        foreach (var entry in entries)
            GlossaryEntries.Add(entry);
        GlossaryStatsText = $"术语库: {GlossaryEntries.Count} 条";
    }

    /// <summary>
    /// Import glossary entries from Excel or JSON.
    /// </summary>
    [RelayCommand]
    private async Task ImportGlossaryAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "术语库文件|*.json;*.xlsx|JSON 文件|*.json|Excel 文件|*.xlsx",
            Title = "导入术语库"
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
        StatusMessage = $"术语库已导入: {GlossaryEntries.Count} 条";
    }

    #endregion

    #region DWG Import

    [RelayCommand]
    private async Task ImportDwgAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "DWG 文件|*.dwg|所有文件|*.*",
            Title = "选择 DWG 文件",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        var filePaths = dialog.FileNames;
        if (filePaths.Length == 0) return;

        IsProcessing = true;
        StatusMessage = $"正在读取 {filePaths.Length} 个 DWG 文件...";

        try
        {
            var allEntities = await Task.Run(() =>
            {
                var result = new List<TextEntity>();
                foreach (var path in filePaths)
                {
                    try
                    {
                        var entities = _dwgReaderService.ExtractFromFile(path);
                        foreach (var e in entities)
                            e.Notes = $"Source: {Path.GetFileNameWithoutExtension(path)}";
                        result.AddRange(entities);
                    }
                    catch (Exception ex)
                    {
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
            StatusMessage = $"已导入 {allEntities.Count} 个文本实体 (来自 {filePaths.Length} 个文件)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"DWG 导入错误: {ex.Message}";
            MessageBox.Show($"导入 DWG 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsProcessing = false; }
    }

    #endregion

    #region Translation

    [RelayCommand]
    private async Task TranslateAsync()
    {
        if (!_licenseService.CanExecuteOperation())
        {
            StatusMessage = "授权无效或体验次数已用完，请先激活";
            PromptForActivation();
            return;
        }

        var entitiesToTranslate = Entities
            .Where(e => !e.IsXref && e.Status == TranslationStatus.Pending)
            .ToList();

        if (entitiesToTranslate.Count == 0)
        {
            StatusMessage = "没有待翻译的文本。请先导入 DWG 或切换语言方向。";
            return;
        }

        if (string.IsNullOrEmpty(_config.DeepSeekApiKey))
        {
            StatusMessage = "请先配置 API Key";
            MessageBox.Show("请在设置中配置 DeepSeek API Key", "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsProcessing = true;
        _cts = new CancellationTokenSource();
        TranslatedCount = 0;
        FailedCount = 0;
        CacheHitCount = 0;
        ProgressValue = 0;
        var totalCount = entitiesToTranslate.Count;
        var completedCount = 0;

        StatusMessage = $"🔄 翻译中 (智能并发) — 0/{totalCount} ...";

        try
        {
            var systemPrompt = LoadSystemPrompt();
            EnsureDeepSeekClient();
            var translationService = new TranslationService(
                _glossaryService, _formatCodeParser, _deepSeekClient!, systemPrompt,
                _config.BatchSize, _config.MaxRetryCount, _consistencyService, maxConcurrency: 5);

            // Use Progress<T> to receive each translation result as it completes
            var progress = new Progress<TranslationPair>(pair =>
            {
                // Marshal to UI thread
                Application.Current.Dispatcher.Invoke(() =>
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
                    StatusMessage = $"🔄 翻译中 (5 并发) — {completedCount}/{totalCount} ...";
                });
            });

            await translationService.TranslateBatchWithProgressAsync(
                entitiesToTranslate, CurrentSourceLang, CurrentTargetLang, progress, _cts.Token);

            ApplyFilter();
            UpdateStatistics();
            StatusMessage = $"✅ 翻译完成: {TranslatedCount} 成功, {FailedCount} 失败 | {LanguageDirection} | 速度约 5 倍于串行";
        }
        catch (TaskCanceledException)
        {
            StatusMessage = "翻译超时 — 请检查网络连接或 API Key";
            MessageBox.Show("翻译请求超时（5 分钟）。\n\n可能原因:\n• 网络连接不稳定\n• API Key 无效\n• DeepSeek 服务暂时不可用\n\n请打开「设置」→「测试连接」验证 API。", "请求超时", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"API 错误: {ex.Message}";
            MessageBox.Show($"API 请求失败:\n\n{ex.Message}\n\n请检查:\n• 设置中的 API Key 是否正确\n• 网络是否可以访问 api.deepseek.com", "API 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "翻译已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"翻译错误: {ex.Message}";
            MessageBox.Show($"翻译失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void CancelTranslate()
    {
        _cts?.Cancel();
        StatusMessage = "正在停止翻译...";
    }

    [RelayCommand]
    private void CancelExport()
    {
        _exportCts?.Cancel();
        StatusMessage = "正在取消导出...";
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        var failedEntities = Entities.Where(e => e.Status == TranslationStatus.TranslationFailed).ToList();
        if (failedEntities.Count == 0) { StatusMessage = "没有失败的翻译需要重试"; return; }

        foreach (var e in failedEntities) { e.Status = TranslationStatus.Pending; e.TranslatedText = string.Empty; }
        UpdateStatistics();
        await TranslateAsync();
    }

    #endregion

    #region Excel I/O

    [RelayCommand]
    private async Task ExportExcelAsync()
    {
        if (Entities.Count == 0) { StatusMessage = "没有可导出的数据"; return; }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel 文件|*.xlsx",
            Title = "保存翻译 Excel 校对表",
            FileName = $"translations_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };
        if (dialog.ShowDialog() != true) return;

        IsProcessing = true;
        StatusMessage = "正在导出 Excel...";
        try
        {
            await _excelService.ExportToExcelAsync(Entities.ToList(), dialog.FileName);
            StatusMessage = $"已导出 {Entities.Count} 条到 {Path.GetFileName(dialog.FileName)} — 可在 Excel 中校对后重新导入";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出错误: {ex.Message}";
        }
        finally { IsProcessing = false; }
    }

    [RelayCommand]
    private async Task ImportExcelAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel 文件|*.xlsx;*.xls|所有文件|*.*",
            Title = "选择翻译校对后的 Excel"
        };
        if (dialog.ShowDialog() != true) return;

        IsProcessing = true;
        StatusMessage = "正在从 Excel 导入...";
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
            StatusMessage = $"已从 Excel 导入并更新 {updatedCount} 条";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入错误: {ex.Message}";
        }
        finally { IsProcessing = false; }
    }

    #endregion

    #region DWG Export (核心功能)

    /// <summary>
    /// Export translated DWG file.
    /// If AutoCAD is available, offers high-precision writeback via COM.
    /// Otherwise falls back to ACadSharp offline writeback.
    /// </summary>
    [RelayCommand]
    private async Task ExportDwgAsync()
    {
        if (!_licenseService.CanExecuteOperation())
        {
            StatusMessage = "授权无效或体验次数已用完，请先激活";
            PromptForActivation();
            return;
        }

        var entitiesToWrite = Entities
            .Where(e => e.Status == TranslationStatus.Translated || e.Status == TranslationStatus.Reviewed)
            .ToList();

        if (entitiesToWrite.Count == 0)
        {
            StatusMessage = "没有已翻译/已审阅的文本可导出 DWG。请先翻译并审核。";
            MessageBox.Show("请先完成翻译或手动审核后再导出 DWG。\n\n提示：在译文列双击可直接编辑翻译。",
                "无数据", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? sourceFilePath = null;
        if (!string.IsNullOrEmpty(_lastSourceFilePath) && File.Exists(_lastSourceFilePath))
            sourceFilePath = _lastSourceFilePath;
        else
        {
            var dialog = new OpenFileDialog { Filter = "DWG 文件|*.dwg", Title = "选择原始 DWG 文件" };
            if (dialog.ShowDialog() == true) sourceFilePath = dialog.FileName;
        }

        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
        { StatusMessage = "请先选择原始 DWG 文件"; return; }

        var dialog2 = new SaveFileDialog
        {
            Filter = "DWG 文件|*.dwg",
            Title = "保存翻译后的 DWG",
            FileName = Path.GetFileNameWithoutExtension(sourceFilePath) + "_translated.dwg"
        };
        if (dialog2.ShowDialog() != true) return;

        IsProcessing = true;
        _exportCts = new CancellationTokenSource();
        StatusMessage = "正在导出翻译后的 DWG...";
        ProgressValue = 0;

        try
        {
            Core.Services.DwgWriteResult result;
            bool usedAcadInterop = false;

            // Show export mode selection dialog
            var modeDialog = new Views.ExportModeDialog(IsAutoCADAvailable())
            {
                Owner = Application.Current.MainWindow
            };

            if (modeDialog.ShowDialog() != true)
            {
                StatusMessage = "已取消导出";
                return;
            }

            if (modeDialog.SelectedMode == Views.ExportModeDialog.ExportMode.AutoCAD)
            {
                usedAcadInterop = true;

                // Pre-alert user about security dialog
                MessageBox.Show(
                    "即将通过 AutoCAD COM 进行精确回写。\n\n" +
                    "重要提示：\n" +
                    "AutoCAD 可能会弹出「是否加载来自非信任路径的程序集」安全对话框。\n" +
                    "该对话框可能在 AutoCAD 窗口后面，请切换到 AutoCAD 窗口查看。\n\n" +
                    "请点击「始终加载」或「加载」以允许插件运行。\n" +
                    "如果不点击，回写将无法完成。",
                    "AutoCAD 安全提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                result = await Task.Run(() =>
                    ExecuteAutoCADWriteback(sourceFilePath, dialog2.FileName, entitiesToWrite),
                    _exportCts?.Token ?? CancellationToken.None);
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

                string modeText = usedAcadInterop ? "AutoCAD 精确回写" : "离线回写";
                StatusMessage = $"DWG 导出完成 ({modeText}): {result.SuccessCount} 条已替换 → {Path.GetFileName(dialog2.FileName)}";
                MessageBox.Show(
                    $"DWG 导出完成!\n\n模式: {modeText}\n已替换: {result.SuccessCount} 条文本\n失败: {result.FailCount} 条\n\n文件: {dialog2.FileName}",
                    "导出成功",
                    MessageBoxButton.OK,
                    result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            else
            {
                var errorMsg = string.Join("\n", result.Errors.Take(5));
                StatusMessage = $"DWG 导出失败: 0 条替换成功";
                MessageBox.Show($"DWG 导出失败:\n{errorMsg}", "导出错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "导出已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"DWG 导出失败: {ex.Message}";
            MessageBox.Show($"导出 DWG 失败: {ex.Message}\n\n提示: ACadSharp 写入功能仍为实验性，可能产生需修复的文件。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsProcessing = false; _exportCts?.Dispose(); _exportCts = null; }
    }

    #region AutoCAD COM Interop

    /// <summary>
    /// Check if AutoCAD is available via COM and configuration.
    /// Uses configured AutoCAD path (if set) or registry detection.
    /// </summary>
    private bool IsAutoCADAvailable()
    {
        // 1. Check configured path
        var configuredPath = _config.AutoCadInstallPath;
        if (!string.IsNullOrEmpty(configuredPath) && Core.Services.AutoCadDetector.IsValidAutoCadPath(configuredPath))
        {
            // Configured path is valid, now check if AutoCAD is running via COM
            return IsAutoCADRunning();
        }

        // 2. Auto-detect from registry
        var detection = Core.Services.AutoCadDetector.DetectInstallation();
        if (detection.Found)
        {
            // Auto-save detected path for future use
            _config.AutoCadInstallPath = detection.InstallPath;
            return IsAutoCADRunning();
        }

        // 3. Fallback: try COM ProgID directly
        return IsAutoCADRunning();
    }

    private static bool IsAutoCADRunning()
    {
        // 通过进程名检测 AutoCAD / AutoCAD LT 是否正在运行
        // （Marshal.GetActiveObject 在 .NET 8 不可用，且 COM 连接可能因权限问题失败）
        try
        {
            if (Process.GetProcessesByName("acad").Length > 0) return true;
            if (Process.GetProcessesByName("acadlt").Length > 0) return true;
        }
        catch { }

        return false;
    }

    private static readonly string[] AcadProgIDs = new[]
    {
        "AutoCAD.Application",
        "AutoCAD.Application.25",      // 2026
        "AutoCAD.Application.24.3",    // 2025
        "AutoCAD.Application.24.2",    // 2024
        "AutoCAD.Application.24.1",    // 2023
        "AutoCAD.Application.24",      // 2022
        "AutoCAD.Application.23",      // 2021
        "AutoCAD.Application.22",      // 2020
        "AutoCADLT.Application",
        "AutoCADLT.Application.25",
        "AutoCADLT.Application.24.3",
        "AutoCADLT.Application.24.2",
        "AutoCADLT.Application.24.1",
        "AutoCADLT.Application.24",
    };

    private Core.Services.DwgWriteResult ExecuteAutoCADWriteback(
        string sourceFilePath,
        string outputFilePath,
        List<TextEntity> entities)
    {
        var result = new Core.Services.DwgWriteResult();
        string? jsonPath = null;

        try
        {
            // Serialize a config JSON that contains source/output paths + entities
            // This way WritebackCommand only needs ONE argument (the config path)
            jsonPath = Path.Combine(Path.GetTempPath(), $"dwgtranslate_{Guid.NewGuid():N}.json");
            var configObj = new
            {
                SourceDwgPath = sourceFilePath,
                OutputDwgPath = outputFilePath,
                Entities = entities,
                CnToEn = IsCnToEn
            };
            var json = System.Text.Json.JsonSerializer.Serialize(configObj, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(jsonPath, json);

            // Try multiple ProgIDs to connect to AutoCAD
            Type? acadType = null;
            string? triedProgID = null;
            Exception? lastException = null;
            foreach (var progId in AcadProgIDs)
            {
                try
                {
                    triedProgID = progId;
                    acadType = Type.GetTypeFromProgID(progId, false);
                    if (acadType != null)
                    {
                        Log.Information("Found AutoCAD COM ProgID: {ProgID}", progId);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (acadType == null)
            {
                var msg = "无法连接 AutoCAD：注册表中找不到任何已知的 AutoCAD COM ProgID。" +
                          "\n\n可能原因：" +
                          "\n1. AutoCAD 未安装或安装不完整" +
                          "\n2. AutoCAD 的 COM 支持未启用（某些精简版/OEM 版本不支持 COM）" +
                          "\n3. 使用的是 AutoCAD LT（不支持 .NET 插件 NETLOAD）" +
                          "\n\n建议：使用「离线导出 DWG」功能，无需 AutoCAD 运行。";
                result.Errors.Add(msg);
                return result;
            }

            dynamic acad = Activator.CreateInstance(acadType)!;
            acad.Visible = true;

            // Detect AutoCAD version from ProgID for compatibility check
            string acadVersion = triedProgID ?? "unknown";
            Log.Information("Connected to AutoCAD via ProgID: {ProgID}", acadVersion);

            var doc = acad.ActiveDocument;
            if (doc == null)
            {
                result.Errors.Add("AutoCAD 没有活动文档");
                return result;
            }

            // Locate the Cad plugin DLL — priority: config > auto-detect > fallback
            string? cadDllPath = ResolveCadPluginPath();

            if (string.IsNullOrEmpty(cadDllPath) || !File.Exists(cadDllPath))
            {
                result.Errors.Add("找不到 DwgTranslator.Cad.dll 插件文件。\n请在「设置」→「AutoCAD 配置」中指定插件路径。");
                return result;
            }

            Log.Information("Using Cad plugin: {Path}", cadDllPath);

            // Step 1: Write config JSON to a fixed known path (no env vars, no cross-process issues)
            var configDir = Path.Combine(Path.GetTempPath(), "DwgTranslator");
            Directory.CreateDirectory(configDir);
            var fixedConfigPath = Path.Combine(configDir, "writeback_config.json");
            var doneSignalPath = Path.Combine(configDir, "writeback_done.txt");

            // Clean up previous signal files
            try { if (File.Exists(doneSignalPath)) File.Delete(doneSignalPath); } catch { }

            // Write config to fixed path (WritebackCommand reads from here)
            File.WriteAllText(fixedConfigPath, json);
            jsonPath = fixedConfigPath; // Track for cleanup
            Log.Information("Config written to fixed path: {Path}", fixedConfigPath);

            // Step 2: Add DLL directory to AutoCAD Trusted Paths (permanent, avoids security dialog)
            try
            {
                AddTrustedPath(acad, cadDllPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to add trusted path (security dialog may still appear)");
            }

            // Step 3: Create a LISP file that loads the DLL and runs the writeback command
            // LISP (command ...) is synchronous — it waits for each command to complete before continuing
            var lspPath = Path.Combine(configDir, "dwgtranslate_exec.lsp");
            var lispDllPath = cadDllPath.Replace("\\", "\\\\");

            // Build LISP script:
            // - Print diagnostic messages to command line
            // - NETLOAD the plugin DLL
            // - Execute DwgTranslateWrite command (no args — reads config from fixed path)
            // - (princ) suppresses the nil return value
            // NOTE: Do NOT add "" args after no-arg commands — they interfere with execution
            var lspContent = $@"(princ ""\nDwgTranslator: loading plugin..."")
(command ""_.NETLOAD"" ""{lispDllPath}"" )
(princ ""\nDwgTranslator: executing writeback..."")
(command ""_.DwgTranslateWrite"" )
(princ ""\nDwgTranslator: done."")
(princ)
";
            File.WriteAllText(lspPath, lspContent);
            Log.Information("Created LISP file: {Path}", lspPath);

            // Step 4: Load and execute the LISP file via SendCommand
            var lispLspPath = lspPath.Replace("\\", "\\\\");
            doc.SendCommand($"(load \"{lispLspPath}\") ");

            // Step 5: Wait for the command to complete (monitor the done signal file)
            // WritebackCommand creates writeback_done.txt when finished
            Log.Information("Waiting for WritebackCommand to complete...");
            int maxWaitSeconds = 120;
            int waited = 0;
            while (waited < maxWaitSeconds)
            {
                System.Threading.Thread.Sleep(2000);
                waited += 2;

                // Check if the done signal file has been created by WritebackCommand
                if (File.Exists(doneSignalPath))
                {
                    Log.Information("WritebackCommand completed (done signal detected)");
                    try
                    {
                        var doneContent = File.ReadAllText(doneSignalPath);
                        if (!doneContent.StartsWith("success|", StringComparison.OrdinalIgnoreCase))
                        {
                            result.SuccessCount = 0;
                            result.Errors.Add($"AutoCAD 回写失败。信号: {doneContent}");
                            StatusMessage = "AutoCAD 回写失败，请检查 AutoCAD 命令行。";
                        }
                        else
                        {
                            result.SuccessCount = entities.Count;
                            StatusMessage = "AutoCAD 精确回写已完成。";
                        }
                    }
                    catch
                    {
                        // Can't read file, assume failure
                        result.SuccessCount = 0;
                        result.Errors.Add("无法读取 AutoCAD 完成信号文件。");
                    }
                    break;
                }

                // Also check if the output DWG file has been created recently
                if (File.Exists(outputFilePath))
                {
                    try
                    {
                        var fi = new FileInfo(outputFilePath);
                        if (fi.Length > 1000 && fi.LastWriteTime > DateTime.Now.AddSeconds(-10))
                        {
                            Log.Information("Output DWG detected, writeback likely complete");
                            result.SuccessCount = entities.Count;
                            StatusMessage = "AutoCAD 精确回写已完成。";
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (result.SuccessCount == 0 && waited >= maxWaitSeconds)
            {
                Log.Warning("WritebackCommand timed out after {Sec}s", maxWaitSeconds);
                result.Errors.Add("AutoCAD 回写超时。可能原因：\n" +
                                  "1. 安全对话框未点击「始终加载」\n" +
                                  "2. AutoCAD 正在处理大型文件\n" +
                                  "3. 命令未成功执行\n" +
                                  $"4. 请检查 AutoCAD 命令行是否有错误提示\n" +
                                  $"配置文件位置: {fixedConfigPath}");
            }

            // Cleanup signal file
            try { if (File.Exists(doneSignalPath)) File.Delete(doneSignalPath); } catch { }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AutoCAD COM writeback failed");
            result.Errors.Add($"AutoCAD 回写失败: {ex.Message}");
        }
        finally
        {
            // Config file at fixed path is cleaned up by WritebackCommand on success.
            // On failure, it's overwritten on next run anyway (Directory.CreateDirectory + WriteAllText).
        }

        return result;
    }

    /// <summary>
    /// Adds the DLL directory to AutoCAD's Trusted Paths so the security dialog is suppressed.
    /// This is a one-time permanent setting that persists across AutoCAD sessions.
    /// </summary>
    private static void AddTrustedPath(dynamic acad, string dllPath)
    {
        try
        {
            var dllDir = Path.GetDirectoryName(dllPath);
            if (string.IsNullOrEmpty(dllDir)) return;

            // Ensure trailing backslash for AutoCAD trusted path format
            if (!dllDir.EndsWith("\\")) dllDir += "\\";

            dynamic prefs = acad.Preferences;
            dynamic files = prefs.Files;
            string? currentTrusted = files.TrustedPath;

            if (currentTrusted != null && currentTrusted.Contains(dllDir, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("Trusted path already contains: {Dir}", dllDir);
                return;
            }

            string newTrusted = string.IsNullOrEmpty(currentTrusted)
                ? dllDir
                : currentTrusted + ";" + dllDir;

            files.TrustedPath = newTrusted;
            Log.Information("Added trusted path: {Dir}", dllDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to add trusted path");
        }
    }
    /// </summary>
    private string? ResolveCadPluginPath()
    {
        // 1. Use configured path
        if (!string.IsNullOrEmpty(_config.CadPluginPath) && File.Exists(_config.CadPluginPath))
            return _config.CadPluginPath;

        // 2. Auto-detect using AutoCadDetector
        var detected = Core.Services.AutoCadDetector.FindCadPlugin();
        if (detected != null)
            return detected;

        // 3. Fallback: look in exe directory and solution output
        //    exe is at: src\DwgTranslator.App\bin\Debug\net8.0-windows\
        //    cad dll is at: src\DwgTranslator.Cad\bin\Debug\net8.0\
        string cadDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DwgTranslator.Cad.dll");
        if (File.Exists(cadDllPath))
            return cadDllPath;

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        // Go up 4 levels: net8.0-windows -> Debug -> bin -> DwgTranslator.App -> src
        string srcDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));
        foreach (var config in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(srcDir, "DwgTranslator.Cad", "bin", config, "net8.0", "DwgTranslator.Cad.dll");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    #endregion

    #endregion

    #region Operations

    [RelayCommand]
    private void MarkAllReviewed()
    {
        int count = 0;
        foreach (var entity in Entities.Where(e => e.Status == TranslationStatus.Translated))
        { entity.Status = TranslationStatus.Reviewed; count++; }
        UpdateStatistics();
        ApplyFilter();
        StatusMessage = $"已将 {count} 条标记为已审阅 — 可以导出 DWG 了";
    }

    [RelayCommand]
    private void ClearAll()
    {
        Entities.Clear();
        FilteredEntities.Clear();
        TotalCount = 0; TranslatedCount = 0; FailedCount = 0;
        GlossaryHitCount = 0; CacheHitCount = 0; VisibleCount = 0;
        ProgressValue = 0; _lastSourceFilePath = null;
        StatusMessage = "已清空";
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
        FilterStatusText = status ?? "全部";
        // ApplyFilter is already called by OnFilterStatusTextChanged
    }

    [RelayCommand]
    private void ApplySearch() => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<TextEntity> filtered = Entities;
        if (FilterStatusText != "全部")
        {
            filtered = FilterStatusText switch
            {
                "待翻译" => filtered.Where(e => e.Status == TranslationStatus.Pending),
                "已翻译" => filtered.Where(e => e.Status == TranslationStatus.Translated),
                "审阅完成" => filtered.Where(e => e.Status == TranslationStatus.Reviewed || e.Status == TranslationStatus.WritebackSuccess),
                "翻译失败" => filtered.Where(e => e.Status == TranslationStatus.TranslationFailed),
                "术语命中" => filtered.Where(e => e.GlossaryHit),
                "已跳过" => filtered.Where(e => e.Status == TranslationStatus.Skipped),
                _ => filtered
            };
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

    [RelayCommand]
    private void Settings()
    {
        var dialog = new Views.SettingsDialog();
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
                StatusMessage = "API 设置已更新";
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
            "您的体验次数已用完或授权已过期。\n\n" +
            "点击「是」打开授权管理窗口进行激活。\n" +
            "点击「否」继续使用受限功能（仅可查看和编辑，不可导出）。",
            "需要激活",
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
        _consistencyService.FlushCache();
        _httpClient?.Dispose();
    }

    #endregion
}