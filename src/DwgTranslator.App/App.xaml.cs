using DwgTranslator.App.Services;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Logging;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using System.IO;
using System.Windows;

namespace DwgTranslator.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static string AppDataDir { get; } = ProductDataDirectory.Initialize(AppDomain.CurrentDomain.BaseDirectory);

    public static ILicenseService LicenseService { get; private set; } = null!;
    public static IServiceProvider Services { get; private set; } = null!;
    public static Serilog.Core.LoggingLevelSwitch LogLevelSwitch { get; } = new();
    public static ILogStore LogStore { get; private set; } = null!;
    public static CadLogReaderService? CadLogReader { get; private set; }

    /// <summary>
    /// Files passed on the command line, imported by <c>MainWindow</c> once it is loaded.
    /// Cleared after the first import so a second window (if one is ever opened) does not
    /// import them again.
    /// </summary>
    public static string[] StartupFiles { get; private set; } = [];

    /// <summary>
    /// True when the program was started with <c>--env-check</c>, which opens the environment
    /// self-check as soon as the main window is ready. The installers pass it so a fresh install
    /// leads straight to the CAD plugin step.
    /// </summary>
    public static bool OpenEnvironmentCheckOnStart { get; private set; }

    public static void ClearEnvironmentCheckRequest() => OpenEnvironmentCheckOnStart = false;

    public static void ClearStartupFiles() => StartupFiles = [];

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled UI exception");
        // A startup XAML failure has no main window to close; do not leave a hidden process locking the release.
        var recovering = MainWindow is { IsLoaded: true };
        // Graceful degradation only applies when there is a window left to degrade into. MsgUnhandledError
        // promises "自动恢复" and owns a single {0} that is labelled 错误:, so it gets the exception type
        // (never the message or the log path: the privacy rule keeps stack details out of dialogs).
        // MsgFatalError is the one with a 日志位置 slot, so the about-to-exit branch uses it.
        try
        {
            if (recovering)
            {
                MessageBox.Show(
                    Strings.Get("MsgUnhandledError", e.Exception.GetType().Name),
                    Strings.Get("MsgTitleWarning"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    Strings.Get("MsgFatalError", e.Exception.GetType().Name, Path.Combine(AppDataDir, "logs").Replace('\\', '/')),
                    Strings.Get("MsgTitleFatalError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch { /* last resort: ignore if even MessageBox fails */ }
        e.Handled = true;
        if (!recovering) Shutdown(1);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Fatal(ex, "Unhandled domain exception. IsTerminating: {Terminating}", e.IsTerminating);
        if (e.IsTerminating)
        {
            try
            {
                MessageBox.Show(
                    Strings.Get("MsgFatalError", (ex?.Message ?? "Unknown").Replace('\\', '/'), Path.Combine(AppDataDir, "logs").Replace('\\', '/')),
                    Strings.Get("MsgTitleFatalError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Files handed to us by the shell ("Open with" / command line). Keep only ones that
        // exist and that the app can actually import.
        StartupFiles = e.Args
            .Where(arg => !string.IsNullOrWhiteSpace(arg) && arg[0] != '-' && File.Exists(arg))
            .ToArray();
        if (StartupFiles.Length > 0)
            Log.Information("Startup files: {Count}", StartupFiles.Length);

        // "DwgTranslator.exe --env-check" opens straight into the environment self-check. The
        // installers use it right after installing, because that screen carries the one step that
        // has to run on the receiving machine: putting the CAD plugin into the CAD.
        OpenEnvironmentCheckOnStart = e.Args.Any(arg =>
            string.Equals(arg, "--env-check", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-e", StringComparison.OrdinalIgnoreCase));

        // Ensure app data directories exist
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(Path.Combine(AppDataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(AppDataDir, "exports"));
        Directory.CreateDirectory(Path.Combine(AppDataDir, "glossaries"));

        // Configure structured logging
        LogStore = new InMemoryLogStore(capacity: 3000);
        var logPath = Path.Combine(AppDataDir, "logs", "dwgtranslator-.log");

        // Read minimum log level from settings (default: Debug)
        var configuredLogLevel = ReadConfiguredLogLevel();

        LogLevelSwitch.MinimumLevel = configuredLogLevel;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogLevelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console(
                outputTemplate: DwgTranslator.Core.Logging.LogFormatter.ConsoleOutputTemplate)
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,  // 10 MB per file
                retainedFileCountLimit: 14,             // keep 14 days
                rollOnFileSizeLimit: true,
                outputTemplate: DwgTranslator.Core.Logging.LogFormatter.SerilogOutputTemplate)
            .WriteTo.Sink(new UISink(LogStore))
            .CreateLogger();

        Log.Information("Application started. App data: {Dir}", AppDataDir);

        // Start reading CAD plugin log files so they appear in the App's log viewer
        var cadLogDir = Path.Combine(AppDataDir, "logs");
        CadLogReader = new CadLogReaderService(LogStore, cadLogDir);
        Log.Information("CAD log reader started. Monitoring: {Dir}", cadLogDir);

        // First-launch: copy default settings if not present
        var settingsPath = Path.Combine(AppDataDir, "settings.json");
        if (!File.Exists(settingsPath))
        {
            var bundledSettings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            if (File.Exists(bundledSettings))
            {
                File.Copy(bundledSettings, settingsPath);
                Log.Information("Copied default settings to {Path}", settingsPath);
            }
        }

        // Migrate before dependency injection creates the API client.
        try { var defaults = DwgTranslator.Core.Services.SettingsStore.Read(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json")); DwgTranslator.Core.Services.SettingsStore.Migrate(settingsPath, defaults); }
        catch (Exception ex) { Log.Warning(ex, "Configuration migration could not be saved"); }

        // Keep the licensing component available for a future commercial switch,
        // but do not load, validate or mutate license state while it is disabled.
        LicenseService = new LicenseService(AppDataDir);
        if (ReadLicensingEnabled(settingsPath))
        {
            LicenseService.LoadLicense();
            Log.Information("License status: {Status}", LicenseService.CurrentLicense.GetDisplayStatus());
        }
        else
        {
            Log.Information("Licensing is disabled for this build configuration");
        }

        // Customer builds use account login; never prompt for provider credentials.

        // Configure Dependency Injection
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();
    }

    /// <summary>
    /// Reads the MinimumLogLevel from the settings file and maps it to a Serilog LogEventLevel.
    /// Falls back to Debug if the file doesn't exist or the value is invalid.
    /// </summary>
    private static LogEventLevel ReadConfiguredLogLevel()
    {
        try
        {
            var settingsPath = Path.Combine(AppDataDir, "settings.json");
            if (!File.Exists(settingsPath))
            {
                settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            }
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(
                    json, DwgTranslator.Core.Models.AppConfigJson.ReadOptions);
                if (config != null && !string.IsNullOrEmpty(config.MinimumLogLevel))
                {
                    return config.MinimumLogLevel.ToLowerInvariant() switch
                    {
                        "verbose" => LogEventLevel.Verbose,
                        "debug" => LogEventLevel.Debug,
                        "information" or "info" => LogEventLevel.Information,
                        "warning" or "warn" => LogEventLevel.Warning,
                        "error" => LogEventLevel.Error,
                        "fatal" => LogEventLevel.Fatal,
                        _ => LogEventLevel.Debug
                    };
                }
            }
        }
        catch
        {
            // Fall back to Debug on any error
        }
        return LogEventLevel.Debug;
    }

    private static bool ReadLicensingEnabled(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return false;
            var json = File.ReadAllText(settingsPath);
            var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(
                json, DwgTranslator.Core.Models.AppConfigJson.ReadOptions);
            return config?.LicensingEnabled == true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read licensing switch; keeping licensing disabled");
            return false;
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        /// <summary>读一遍 settings.json（多处需要同一份配置：任务参数 / 翻译管线 / 后端客户端）。</summary>
        static DwgTranslator.Core.Models.AppConfig ReadAppConfig()
        {
            try
            {
                var path = Path.Combine(AppDataDir, "settings.json");
                if (!File.Exists(path)) return new DwgTranslator.Core.Models.AppConfig();
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new DwgTranslator.Core.Models.AppConfig();
                return System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(
                           json, DwgTranslator.Core.Models.AppConfigJson.ReadOptions)
                       ?? new DwgTranslator.Core.Models.AppConfig();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 settings.json 失败，改用默认配置");
                return new DwgTranslator.Core.Models.AppConfig();
            }
        }
        // Logging
        services.AddSingleton(LogStore);

        // Localization — load persisted language from settings
        services.AddSingleton<ILocalizationService>(sp =>
        {
            string? persistedLanguage = null;
            try
            {
                var settingsPath = Path.Combine(AppDataDir, "settings.json");
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(
                        json, DwgTranslator.Core.Models.AppConfigJson.ReadOptions);
                    if (config != null && !string.IsNullOrEmpty(config.Language))
                        persistedLanguage = config.Language;
                }
            }
            catch { /* use default language */ }
            return new LocalizationService(persistedLanguage);
        });

        // Core services (concrete types map 1:1 to their interfaces)
        services.AddSingleton<IGlossaryService, GlossaryService>();
        services.AddSingleton<IExcelService, ExcelService>();
        services.AddSingleton<IDwgReaderService, DwgReaderService>();
        services.AddSingleton<IDwgWriterService, DwgWriterService>();
        services.AddSingleton<IDxfReaderService, DwgReaderService>();
        services.AddSingleton<IDxfWriterService, DwgWriterService>();
        services.AddSingleton<IAutoCadInteropService, AutoCadInteropService>();

        // LicenseService needs the AppDataDir parameter
        services.AddSingleton<ILicenseService>(sp => LicenseService);

        // Translation helpers (stateless, safe to share)
        services.AddSingleton<IFormatCodeParser, FormatCodeParser>();
        services.AddSingleton<IFormatCodeRestorer, FormatCodeRestorer>();

        // ── 基础设施：AI 出口与翻译缓存（UI 不再拥有 HttpClient / API Key）────────────
        // 提示词与一致性缓存都收在 Core：任务层与界面共用同一份缓存，否则界面统计的"缓存命中"
        // 与任务层实际使用的缓存是两套数据（一个显示 0 命中，另一个其实没调 API）。
        services.AddSingleton<DwgTranslator.Core.Translation.ITranslationConsistencyService>(sp =>
            new DwgTranslator.Core.Translation.TranslationConsistencyService(
                Path.Combine(AccountWorkspace.DirectoryFor(AppDataDir, ReadAppConfig().ActiveAccountId), "translation_cache.json")));

        // 模型凭据、模型名与系统提示词仅存在于 Worker；桌面端不注册任何直连模型客户端。


        /// <summary>
        /// 返回当前安装实例的稳定设备标识。使用独立文件而不是机器名，避免改名后
        /// 被后端误判为新设备；文件内容只是一枚随机 UUID，不包含硬件指纹。
        /// </summary>
        static string GetStableDeviceId()
        {
            try { return InstallationIdentityStore.GetOrCreate(Path.Combine(AppDataDir, "device-id")); }
            catch (Exception ex)
            {
                Log.Warning(ex, "无法读取或保存设备标识，已禁用 APP 登录，未生成临时设备编号");
                return string.Empty;
            }
        }
        // ViewModel
                // ── 任务层（TaskManager）──────────────────────────────────────────────
        // UI 通过 ITaskManager 提交/取消/重试任务并订阅事件，按钮事件里不再直接跑解析与翻译。
        // 并发参数来自用户配置：本地并发（同时处理的图纸数）与 AI 并发（单图内请求数）分开限流。
        services.AddSingleton<DwgTranslator.Core.Tasks.ITaskStore>(sp =>
            new DwgTranslator.Core.Tasks.JsonTaskStore(Path.Combine(AccountWorkspace.DirectoryFor(AppDataDir, ReadAppConfig().ActiveAccountId), "tasks.json")));

        services.AddSingleton<DwgTranslator.Core.Tasks.TaskManagerOptions>(sp =>
        {
            var options = new DwgTranslator.Core.Tasks.TaskManagerOptions();
            try
            {
                var settingsPath = Path.Combine(AppDataDir, "settings.json");
                if (File.Exists(settingsPath))
                {
                    var cfg = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(
                        File.ReadAllText(settingsPath), DwgTranslator.Core.Models.AppConfigJson.ReadOptions);
                    if (cfg != null)
                    {
                        options.LocalWorkerCount = cfg.LocalWorkerCount;
                        options.AiConcurrency = cfg.AiConcurrency;
                        options.MaxRetryCount = cfg.MaxRetryCount;
                        options.MemoryOptimization = cfg.MemoryOptimization;
                    }
                }
            }
            catch (Exception ex) { Log.Warning(ex, "Failed to read task options; using defaults"); }
            return options;
        });

        // 任务引擎：解析/翻译/写回三段都在后台跑，图层并发受控，单个任务失败不影响其它任务
        services.AddSingleton<DwgTranslator.Core.Tasks.ITaskManager>(sp =>
        {
            try
            {
                var config = ReadAppConfig();
                var api = sp.GetRequiredService<DwgTranslator.Core.Api.IApiClient>();
                if (!string.Equals(api.ModeName, "worker", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Production task engine requires the Worker API client.");

                var glossary = sp.GetRequiredService<DwgTranslator.Core.Services.IGlossaryService>();
                var restorer = sp.GetRequiredService<DwgTranslator.Core.Translation.IFormatCodeRestorer>();
                var workerTranslation = new DwgTranslator.Core.Services.WorkerTranslationService(
                    api, config, restorer.Restore, () => glossary.GetAllEntries());
                return new DwgTranslator.Core.Tasks.TaskManager(
                    sp.GetRequiredService<DwgTranslator.Core.Services.IDwgReaderService>(),
                    sp.GetService<DwgTranslator.Core.Services.IDxfReaderService>(),
                    workerTranslation,
                    sp.GetRequiredService<DwgTranslator.Core.Services.IDwgWriterService>(),
                    sp.GetService<DwgTranslator.Core.Services.IDxfWriterService>(),
                    sp.GetRequiredService<DwgTranslator.Core.Tasks.ITaskStore>(),
                    sp.GetRequiredService<DwgTranslator.Core.Tasks.TaskManagerOptions>(),
                    config, sp.GetService<DwgTranslator.Core.Services.IAutoCadInteropService>());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Worker task engine could not be constructed");
                throw;
            }
        });
        // ── 后端客户端（IApiClient）────────────────────────────────────────────
        // UI 与 CAD 插件只依赖 IApiClient，不再直接接触 DeepSeek / 模型名 / System Prompt。
        // 生产环境始终走 Worker；旧 apiMode=direct 配置会被工厂强制纠正。
        services.AddSingleton<DwgTranslator.Core.Api.IApiClient>(sp =>
        {
            var config = ReadAppConfig();
            var deviceName = Environment.MachineName;
            var deviceId = GetStableDeviceId();
            return DwgTranslator.Core.Api.ApiClientFactory.Create(
                config, new System.Net.Http.HttpClient(),
                () => DwgTranslator.Core.Models.AppConfig.DecryptApiKey(ReadAppConfig().AuthTokenEncrypted),
                deviceId, deviceName);
        });

        // ViewModel
        services.AddTransient<MainViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application shutting down");
        CadLogReader?.Dispose();
        (Services as IDisposable)?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

