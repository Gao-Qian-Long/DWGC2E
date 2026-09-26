using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using System.IO;
using System.Text.Json;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    private static readonly JsonSerializerOptions ConfigWriteOptions = AppConfigJson.WriteOptions;

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
                // An empty file must not abort start-up: an installer that shipped a 0-byte
                // settings.json used to fail deserialization here and the whole configuration
                // load fell through to the catch block, leaving the program without defaults.
                if (!string.IsNullOrWhiteSpace(json))
                    _config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.ReadOptions) ?? new AppConfig();
                else
                    // An empty settings.json must not fall through to a previously loaded config: by
                    // the time the file is written back further down, that config's API key has
                    // already been DECRYPTED, so carrying it over stores the key in clear text.
                    _config = new AppConfig();
            }

            // API endpoint migration runs before client construction in SettingsStore/ApiClientFactory.
            var settingsChanged = false;
            // Resolve the installed CAD host and bundled plugin automatically. Persist
            // encrypted/raw settings before decrypting the API key in memory.
            if (!AutoCadDetector.IsValidAutoCadPath(_config.AutoCadInstallPath))
            {
                var detection = AutoCadDetector.DetectInstallation();
                if (detection.Found)
                {
                    _config.AutoCadInstallPath = detection.InstallPath;
                    settingsChanged = true;
                }
            }

            // Always prefer the plugin that ships with THIS build. A settings.json can point at a
            // copy left by an older installation (different folder, different version); the app then
            // loads the matching plugin anyway, while the settings dialog shows — and would save —
            // the stale path, which is how a user ends up "fixing" a path that then breaks writeback.
            var bundledPlugin = AutoCadDetector.FindCadPlugin();
            if (!string.IsNullOrWhiteSpace(bundledPlugin) && (string.IsNullOrWhiteSpace(_config.CadPluginPath) || !File.Exists(_config.CadPluginPath)))
            {
                if (!string.Equals(bundledPlugin, _config.CadPluginPath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("CAD plugin resolved to the bundled copy: {Bundled} (configured: {Configured})",
                        bundledPlugin, _config.CadPluginPath);
                    _config.CadPluginPath = bundledPlugin;
                    settingsChanged = true;
                }
            }
            else if (string.IsNullOrWhiteSpace(_config.CadPluginPath) || !File.Exists(_config.CadPluginPath))
            {
                Log.Warning("No CAD plugin found next to the application; CAD writeback will be unavailable");
            }

            if (settingsChanged || !File.Exists(appDataPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(appDataPath)!);
                SettingsStore.Update(appDataPath, latest => {
                    latest.AutoCadInstallPath = _config.AutoCadInstallPath;
                    latest.CadPluginPath = _config.CadPluginPath;
                });
                _settingsPath = appDataPath;
                Log.Information("CAD integration defaults saved: Install={Install}; Plugin={Plugin}",
                    _config.AutoCadInstallPath, _config.CadPluginPath);
            }

            ResolveExportDirectory();
            if (!Path.IsPathRooted(_config.LogDirectory))
                _config.LogDirectory = Path.Combine(App.AppDataDir, _config.LogDirectory);
            if (!Path.IsPathRooted(_config.GlossaryPath))
            {
                var appDataGlossary = Path.Combine(App.AppDataDir, _config.GlossaryPath);
                if (File.Exists(appDataGlossary))
                    _config.GlossaryPath = appDataGlossary;
            }

            if (!string.IsNullOrWhiteSpace(_config.ExportDirectory)) Directory.CreateDirectory(_config.ExportDirectory);

            RefreshGlossaryData();

            // 一致性缓存是 DI 单例（与任务层共用同一份）：这里只做一次落盘，
            // 不再 new 第二个实例——两套缓存会让界面的命中统计与任务层实际用到的数据对不上。
            _consistencyService.FlushCache();

            // 开机启动以注册表实际状态为准：用户可能在任务管理器里禁用过，或者换过绿色版目录。
            // 配置里那个布尔值不能"说谎"（否则开关显示已勾选、重启却不启动）。
            _config.StartWithWindows = DwgTranslator.App.Services.StartupRegistration.IsEnabled();
            ApplyLanguagePair();
            RefreshLicenseStatus();
            // The glossary count has its own place in the status bar; repeating it here made the
            // bar say the same thing twice.
            StatusMessage = Strings.Get("StatusReady");
            _runtimeBaseline = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Config load error");
            StatusMessage = Strings.Get("StatusConfigLoadFailed");
            // 配置读失败时 _config 退回默认值（语言方向、输出目录、并发全是默认）：
            // 会话恢复据此知道"可以用上次记录里的语言方向兜底"，而不是把这个默认方向当成用户选择。
            _configLoadFailed = true;
        }
    }

    /// <summary>settings.json 是否未能读出（见 LoadConfig 的 catch）。</summary>
    private bool _configLoadFailed;

    /// <summary>源图纸目录下的兜底输出子目录名（仅当安装区 exports 不可写时使用）。</summary>
    internal const string DefaultOutputFolderName = "已翻译图纸";

    /// <summary>
    /// 把账号默认输出目录解析进 <c>_config.ExportDirectory</c>。
    /// 旧默认值（AppData\DWGC2E\exports、旧漫游 DwgTranslator\exports）按"未设置"处理；
    /// 没有历史选择时默认落到 <b>&lt;安装目录&gt;\exports</b>（2026-10-02 用户决定，发布即生效、
    /// 无需重装；安装区不可写时退回源图纸旁的 <see cref="DefaultOutputFolderName"/>），
    /// 由用户显式选过的目录（AccountOutputDirectories）始终优先，不会被覆盖。
    /// </summary>
    private void ResolveExportDirectory()
        => _config.ExportDirectory = AccountWorkspace.OutputDirectoryFor(
            _config, App.AppDataDir, SourceDirectoryHint(), DefaultOutputFolderName, App.InstallDir);

    /// <summary>当前队列首张图纸所在目录：导出默认目录的锚点。</summary>
    private string? SourceDirectoryHint()
        => DrawingFiles.Count == 0 ? null : Path.GetDirectoryName(DrawingFiles[0].FullPath);

    private string _appliedLanguagePair = string.Empty;
    private bool _applyingLanguagePair;

    /// <summary>
    /// Pushes the configured language pair into the bindable state. Codes the catalog does not know
    /// are kept verbatim, so a hand-edited settings.json still selects that exact language.
    /// </summary>
    private void ApplyLanguagePair() => ApplyLanguagePair(_config.SourceLanguage, _config.TargetLanguage);

    /// <summary>把指定的一对语言推到界面状态；供配置加载与"上次工作区"兜底共用。</summary>
    private void ApplyLanguagePair(string sourceLanguage, string targetLanguage)
    {
        _applyingLanguagePair = true;
        try
        {
            CurrentSourceLang = TranslationLanguages.Normalize(sourceLanguage);
            CurrentTargetLang = TranslationLanguages.Normalize(targetLanguage);
            if (string.Equals(CurrentSourceLang, CurrentTargetLang, StringComparison.OrdinalIgnoreCase))
                CurrentTargetLang = CurrentSourceLang == "EN" ? "ZH" : "EN";
            _appliedLanguagePair = PairKey(CurrentSourceLang, CurrentTargetLang);
            LanguageDirection = DescribeLanguagePair(CurrentSourceLang, CurrentTargetLang);
        }
        finally { _applyingLanguagePair = false; }
        OnPropertyChanged(nameof(TargetIsCjk));
    }

    private static string PairKey(string source, string target) => $"{source}>{target}";

    /// <summary>Short label such as "简体中文 → 日本語" for the title bar and status messages.</summary>
    private static string DescribeLanguagePair(string source, string target)
    {
        var from = TranslationLanguages.Find(source);
        var to = TranslationLanguages.Find(target);
        return $"{from?.NativeName ?? source} → {to?.NativeName ?? target}";
    }

    partial void OnCurrentSourceLangChanged(string value) => LanguagePairChanged();
    partial void OnCurrentTargetLangChanged(string value) => LanguagePairChanged();

    /// <summary>
    /// Reacts to a language picker: persist the pair, rebuild that pair's glossary, and send the
    /// entities translated in the previous pair back to pending. Without the reset the grid would
    /// keep showing translations for a language the drawing is no longer written into.
    /// </summary>
    private async void LanguagePairChanged()
    {
        if (_applyingLanguagePair) return;
        var key = PairKey(CurrentSourceLang, CurrentTargetLang);
        if (string.Equals(key, _appliedLanguagePair, StringComparison.OrdinalIgnoreCase)) return;

        var proposedSource = CurrentSourceLang;
        var proposedTarget = CurrentTargetLang;
        var previous = _appliedLanguagePair.Split('>');
        if (previous.Length == 2)
        {
            _applyingLanguagePair = true;
            CurrentSourceLang = previous[0]; CurrentTargetLang = previous[1];
            _applyingLanguagePair = false;
            if (IsProcessing || IsGlossaryLoading || !await ConfirmLeaveGlossaryAsync()) return;
            _applyingLanguagePair = true;
            CurrentSourceLang = proposedSource; CurrentTargetLang = proposedTarget;
            _applyingLanguagePair = false;
        }
        _appliedLanguagePair = key;
        _config.SourceLanguage = CurrentSourceLang;
        _config.TargetLanguage = CurrentTargetLang;
        LanguageDirection = DescribeLanguagePair(CurrentSourceLang, CurrentTargetLang);
        OnPropertyChanged(nameof(TargetIsCjk));

        PersistLanguagePair();
        ResetTranslationsForLanguageChange();
        _ = RefreshGlossaryDataAsync();
        StatusMessage = Strings.Get("StatusDirectionSwitched", LanguageDirection);
    }

    /// <summary>Clears translations produced for the previous language pair.</summary>
    private void ResetTranslationsForLanguageChange()
    {
        foreach (var entity in Entities)
        {
            if (entity.Status is TranslationStatus.Translated or TranslationStatus.Reviewed
                or TranslationStatus.WritebackSuccess or TranslationStatus.WritebackFailed)
            {
                entity.Status = TranslationStatus.Pending;
                entity.TranslatedText = string.Empty;
                entity.GlossaryHit = false;
            }
        }

        ApplyFilter();
        UpdateStatistics();
    }

    /// <summary>
    /// Saves the language pair. Only these two fields are written back: the in-memory config holds
    /// the DECRYPTED API key, so serializing it wholesale would store the key in clear text.
    /// </summary>
    private void PersistLanguagePair()
    {
        try
        {
            var path = _settingsPath ?? Path.Combine(App.AppDataDir, "settings.json");
            SettingsStore.Update(path, config => { config.SourceLanguage = CurrentSourceLang; config.TargetLanguage = CurrentTargetLang; });
            _settingsPath = path;
            // 会话记录里的语言方向要跟着走，否则下次启动记的是旧方向（记录里那一对只在
            // settings.json 读不出来时用作兜底，但也不该是错的）。
            ScheduleWorkspaceSessionSave();
            Log.Information("Language pair saved to settings: {Source} -> {Target}", CurrentSourceLang, CurrentTargetLang);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving the language pair failed");
        }
    }

    private void RefreshGlossaryData()
    {
        GlossaryEntries.Clear();
        foreach (var entry in _glossaryService.GetAllEntries())
            GlossaryEntries.Add(entry);
        GlossaryStatsText = Strings.Get("StatsGlossaryCount", GlossaryEntries.Count);
        RefreshGlossaryConflicts();
        if (IsGlossaryPage) LoadTermEditor();
    }

    private readonly System.Threading.SemaphoreSlim _glossaryLoadGate = new(1, 1);
    private async Task RefreshGlossaryDataAsync()
    {
        await _glossaryLoadGate.WaitAsync();
        IsGlossaryLoading = true;
        try
        {
            await _glossaryService.LoadGlossaryAsync(ResolveGlossaryPath());
            RefreshGlossaryData();
        }
        catch (Exception ex)
        {
            TermFeedback = "术语加载失败，请检查文件格式与访问权限后重试。";
            Log.Warning(ex, "Glossary load failed");
        }
        finally { IsGlossaryLoading = false; _glossaryLoadGate.Release(); }
    }

    private string ResolveGlossaryPath()
    {
        EnsureWorkspace();
        return WorkspacePath;
    }

    #endregion
}

