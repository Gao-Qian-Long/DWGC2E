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
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DwgTranslator");

    public static ILicenseService LicenseService { get; private set; } = null!;
    public static IServiceProvider Services { get; private set; } = null!;
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
        // Graceful degradation: show non-blocking warning instead of crashing.
        // Do NOT expose exception details to the user — they are in the log file.
        try
        {
            MessageBox.Show(
                Strings.Get("MsgUnhandledError", Path.Combine(AppDataDir, "logs").Replace('\\', '/')),
                Strings.Get("MsgTitleWarning"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* last resort: ignore if even MessageBox fails */ }
        e.Handled = true;
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

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(configuredLogLevel)
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

        // First-launch: prompt for API key if not configured
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = File.ReadAllText(settingsPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(
                    json, DwgTranslator.Core.Models.AppConfigJson.ReadOptions);
                if (config != null && string.IsNullOrEmpty(config.DeepSeekApiKey))
                {
                    MessageBox.Show(
                        Strings.Get("MsgFirstLaunch"),
                        Strings.Get("MsgTitleFirstSetup"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to check first-launch settings");
            }
        }

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

        // ── 基础设施：AI 出口与翻译缓存（UI 不再拥有 HttpClient / API Key）────────────
        // 提示词与一致性缓存都收在 Core：任务层与界面共用同一份缓存，否则界面统计的"缓存命中"
        // 与任务层实际使用的缓存是两套数据（一个显示 0 命中，另一个其实没调 API）。
        services.AddSingleton<DwgTranslator.Core.Translation.ITranslationConsistencyService>(sp =>
            new DwgTranslator.Core.Translation.TranslationConsistencyService(
                Path.Combine(AppDataDir, "translation_cache.json")));

        // 配置感知的 DeepSeek 客户端：每次调用前重读 settings.json，
        // 用户"先启动程序、再填 Key"的常规流程因此不需要重启。
        services.AddSingleton<DwgTranslator.Core.Services.IDeepSeekClient>(sp =>
            new DwgTranslator.Core.Services.SettingsBackedDeepSeekClient(
                Path.Combine(AppDataDir, "settings.json")));

        // 翻译管线（术语 / 格式码 / 质检 / 重试 / 一致性缓存）。
        // 注册它的直接动机：DirectApiClient 需要它，而 ActivatorUtilities 在缺少该注册时
        // 会抛异常 → IApiClient 静默退化成"未配置的 WorkerApiClient"，界面显示的模式与
        // 实际能力全是错的。任务层不使用这个实例（它按 TaskManagerOptions.AiConcurrency 自建）。
        services.AddSingleton<DwgTranslator.Core.Services.ITranslationService>(sp =>
        {
            var config = ReadAppConfig();
            return new DwgTranslator.Core.Services.TranslationService(
                sp.GetRequiredService<IGlossaryService>(),
                sp.GetRequiredService<IFormatCodeParser>(),
                sp.GetRequiredService<DwgTranslator.Core.Services.IDeepSeekClient>(),
                DwgTranslator.Core.Services.TranslationPrompt.LoadSystemPrompt(
                    AppDataDir, AppDomain.CurrentDomain.BaseDirectory),
                config.BatchSize,
                config.MaxRetryCount,
                sp.GetService<DwgTranslator.Core.Translation.ITranslationConsistencyService>(),
                maxConcurrency: Math.Min(20, Math.Max(1, config.AiConcurrency)));
        });


        /// <summary>
        /// 返回当前安装实例的稳定设备标识。使用独立文件而不是机器名，避免改名后
        /// 被后端误判为新设备；文件内容只是一枚随机 UUID，不包含硬件指纹。
        /// </summary>
        static string GetStableDeviceId()
        {
            var path = Path.Combine(AppDataDir, "device-id");
            try
            {
                Directory.CreateDirectory(AppDataDir);
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (Guid.TryParse(existing, out _)) return existing;
                }

                var created = Guid.NewGuid().ToString("D");
                File.WriteAllText(path, created);
                return created;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "无法持久化设备标识，将使用临时设备标识");
                return Guid.NewGuid().ToString("D");
            }
        }
        // ViewModel
                // ── 任务层（TaskManager）──────────────────────────────────────────────
        // UI 通过 ITaskManager 提交/取消/重试任务并订阅事件，按钮事件里不再直接跑解析与翻译。
        // 并发参数来自用户配置：本地并发（同时处理的图纸数）与 AI 并发（单图内请求数）分开限流。
        services.AddSingleton<DwgTranslator.Core.Tasks.ITaskStore>(sp =>
            new DwgTranslator.Core.Tasks.JsonTaskStore(Path.Combine(AppDataDir, "tasks.json")));

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
                var settingsPath = Path.Combine(AppDataDir, "settings.json");
                var config = File.Exists(settingsPath)
                    ? System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(
                          File.ReadAllText(settingsPath), DwgTranslator.Core.Models.AppConfigJson.ReadOptions)
                      ?? new DwgTranslator.Core.Models.AppConfig()
                    : new DwgTranslator.Core.Models.AppConfig();

                // 系统提示词从 Core 统一加载（界面与任务层共用一份，避免"单文件翻译"与"批量任务"质量不一致）
                var systemPrompt = DwgTranslator.Core.Services.TranslationPrompt.LoadSystemPrompt(
                    AppDataDir, AppDomain.CurrentDomain.BaseDirectory);

                var api = sp.GetRequiredService<DwgTranslator.Core.Api.IApiClient>();
                if (string.Equals(api.ModeName, "worker", StringComparison.OrdinalIgnoreCase))
                {
                    // Reuse only the existing formatter; Worker translation never calls its model client.
                    var formatter = (DwgTranslator.Core.Services.TranslationService)
                        sp.GetRequiredService<DwgTranslator.Core.Services.ITranslationService>();
                    var glossary = sp.GetRequiredService<DwgTranslator.Core.Services.IGlossaryService>();
                    var workerTranslation = new DwgTranslator.Core.Services.WorkerTranslationService(
                        api, config, formatter.RestoreFormatCodes, () => glossary.GetAllEntries());
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
                return new DwgTranslator.Core.Tasks.TaskManager(
                    sp.GetRequiredService<DwgTranslator.Core.Services.IDwgReaderService>(),
                    sp.GetService<DwgTranslator.Core.Services.IDxfReaderService>(),
                    sp.GetRequiredService<DwgTranslator.Core.Services.IGlossaryService>(),
                    sp.GetRequiredService<DwgTranslator.Core.Translation.IFormatCodeParser>(),
                    sp.GetRequiredService<DwgTranslator.Core.Services.IDeepSeekClient>(),
                    sp.GetRequiredService<DwgTranslator.Core.Services.IDwgWriterService>(),
                    sp.GetService<DwgTranslator.Core.Services.IDxfWriterService>(),
                    sp.GetRequiredService<DwgTranslator.Core.Tasks.ITaskStore>(),
                    sp.GetRequiredService<DwgTranslator.Core.Tasks.TaskManagerOptions>(),
                    config,
                    systemPrompt,
                    sp.GetService<DwgTranslator.Core.Services.IAutoCadInteropService>(),
                    sp.GetService<DwgTranslator.Core.Translation.ITranslationConsistencyService>());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Task engine could not be constructed; the legacy single-file flow stays in use");
                throw;
            }
        });
        // ── 后端客户端（IApiClient）────────────────────────────────────────────
        // UI 与 CAD 插件只依赖 IApiClient，不再直接接触 DeepSeek / 模型名 / System Prompt。
        // 走 Worker 还是直连由 settings.json 的 apiMode 决定，切换不需要改代码。
        services.AddSingleton<DwgTranslator.Core.Api.IApiClient>(sp =>
        {
            var config = ReadAppConfig();
            var deviceName = Environment.MachineName;
            var deviceId = GetStableDeviceId();
            return DwgTranslator.Core.Api.ApiClientFactory.Create(
                config, new System.Net.Http.HttpClient(),
                () => DwgTranslator.Core.Models.AppConfig.DecryptApiKey(ReadAppConfig().AuthTokenEncrypted),
                deviceId, deviceName,
                () => ActivatorUtilities.CreateInstance<DwgTranslator.Core.Api.DirectApiClient>(sp));
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
