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
    /// <summary>安装目录（exe 所在目录）：默认输出 &lt;安装目录&gt;\exports 的锚点（2026-10-02 用户决定）。</summary>
    public static string InstallDir { get; } = AppDomain.CurrentDomain.BaseDirectory;

    public static ILicenseService LicenseService { get; private set; } = null!;
    public static IServiceProvider Services { get; private set; } = null!;
    public static Serilog.Core.LoggingLevelSwitch LogLevelSwitch { get; } = new();
    public static ILogStore LogStore { get; private set; } = null!;
    public static CadLogReaderService? CadLogReader { get; private set; }

    /// <summary>
    /// settings.json 读取失败的可见记录（null = 本次启动没有遇到）。这条信息必须能走到界面上：
    /// 失败发生在 DI 容器装配期间（ViewModel 还不存在），而且日志里只有一行 Warning，
    /// 用户看到的是"界面照常、设置悄悄变成默认值"。先在这里攒下来，等
    /// <c>MainViewModel.InitializeAsync</c> 起来后再随启动降级报告一起告诉用户。
    /// </summary>
    public static string? SettingsReadWarning { get; private set; }

    /// <summary>
    /// 同一会话内未处理 UI 异常的次数。
    /// 阈值取 3 的理由：1~2 次通常是"一次性"故障（一个已释放的对象穿帮、一次绑定、一段动画），
    /// 为此直接重启会丢掉用户正在做的校对；连续 3 次说明界面状态已经不可信——每次异常都跳过了
    /// 那一小段工作，异常还会继续冒出来——此时才提示重启，并让用户自己决定何时重启。
    /// </summary>
    private const int RepeatedFailureRestartThreshold = 3;
    private int _unhandledUiExceptionCount;
    private bool _restartPromptIssued;

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
        // 完整异常链（含堆栈）必须落到日志里：这是排查"莫名其妙的小 bug"的唯一线索。
        // 级别用 Error 而不是 Fatal——进程并没有结束，把一次已恢复的异常记成致命错误会让日志面板
        // 每次都为同一类瞬态故障报红，反而把真正致命的记录淹掉。
        var count = ++_unhandledUiExceptionCount;
        Log.Error(e.Exception, "Unhandled UI exception #{Count}; windowLoaded={Loaded}", count, MainWindow is { IsLoaded: true });

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
                // 反复出错时只提示一次：每次异常都弹一次模态框会把界面堵死，用户反而没法保存手上的工作。
                if (count >= RepeatedFailureRestartThreshold && !_restartPromptIssued)
                {
                    _restartPromptIssued = true;
                    PromptRestartAfterRepeatedFailures(count, e.Exception);
                }
                else if (count < RepeatedFailureRestartThreshold)
                {
                    MessageBox.Show(
                        Strings.Get("MsgUnhandledError", e.Exception.GetType().Name),
                        Strings.Get("MsgTitleWarning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
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

    /// <summary>
    /// 反复出错后的重启入口：解释"为什么会看到这个提示"，并把是否重启交给用户。
    /// 不自动重启——用户可能正在校对，重启会丢掉未保存的修改。
    /// </summary>
    private void PromptRestartAfterRepeatedFailures(int count, Exception failure)
    {
        var answer = MessageBox.Show(
            $"应用本次运行已遇到 {count} 次未处理错误，界面状态可能已经不可靠。\n\n"
            + $"错误: {failure.GetType().Name}\n\n"
            + "建议立即重启应用。重启会中断正在执行的任务（任务记录已保存，可继续），"
            + "未保存的校对修改会丢失。\n\n是否现在重启？",
            Strings.Get("MsgTitleWarning"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) RestartApplication();
    }

    /// <summary>重新拉起本进程（单文件发布下 <c>Environment.ProcessPath</c> 就是原始 exe），然后退出当前进程。</summary>
    private static void RestartApplication()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                Log.Warning("无法确定可执行文件路径，跳过自动重启，请手动重新打开应用");
            }
            else
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = true };
                // 带上原始命令行参数，避免重启后丢掉"打开方式"传进来的图纸。
                foreach (var argument in Environment.GetCommandLineArgs().Skip(1)) startInfo.ArgumentList.Add(argument);
                System.Diagnostics.Process.Start(startInfo);
                Log.Information("应用已按用户要求重启：{Executable}", executable);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动重启失败，请手动重新打开应用");
        }
        Current.Shutdown(2);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Fatal(ex, "Unhandled domain exception. IsTerminating: {Terminating}", e.IsTerminating);
        if (e.IsTerminating)
        {
            try
            {
                // 槽位语义与 MsgFatalError 模板一致：{0} = 错误描述（异常类型 + 原始消息），
                // {1} = 日志位置。原先把消息当路径做了 '\' -> '/' 归一化，等于把消息里的路径
                // 改成了斜杠，既没用又读起来像路径。
                var description = ex == null ? "Unknown" : $"{ex.GetType().Name}: {ex.Message}";
                MessageBox.Show(
                    Strings.Get("MsgFatalError", description, Path.Combine(AppDataDir, "logs").Replace('\\', '/')),
                    Strings.Get("MsgTitleFatalError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
            return;
        }

        // CLR 还活着，但没有任何用户可见的出口：后台线程上的故障会被记进日志文件而已，
        // 用户看到的是"程序有时候就是不对"。这里补一次可见提示（回调在工作线程上，必须切到 UI 线程）。
        if (ex == null) return;
        try
        {
            Dispatcher.Invoke(() => MessageBox.Show(
                Strings.Get("MsgUnhandledError", ex.GetType().Name),
                Strings.Get("MsgTitleWarning"), MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        catch (Exception notifyFailure)
        {
            Log.Debug(notifyFailure, "非终止异常的用户提示无法显示（可能正在退出）");
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
        // 注意：不再预建 AppData\exports —— 默认输出已改为 <安装目录>\exports（2026-10-02），
        // AppData 下不留任何会被误认成"默认输出"的空目录。
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(Path.Combine(AppDataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(AppDataDir, "glossaries"));

        // Configure structured logging
        LogStore = new InMemoryLogStore(capacity: 3000);
        var logPath = Path.Combine(AppDataDir, "logs", "dwgtranslator-.log");

        // Read minimum log level from settings (default: Debug)
        var configuredLogLevel = ReadConfiguredLogLevel(out var logLevelWarning);

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

        // Logger 已经就绪，这时候报"日志级别没读到"才有人看得见（日志文件 + 界面日志面板）。
        if (logLevelWarning != null)
            Log.Warning("日志级别配置读取失败，已回退到 Debug：{Detail}", logLevelWarning);

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
    /// <para>
    /// 读取失败的原因通过 <paramref name="warning"/> 带出去，而不是在这里写日志：本方法在
    /// <c>Log.Logger</c> 建立之前运行，此刻写日志会落到静默 logger 上，文件与日志面板都看不到
    /// （旧实现就是一个空的 catch，用户只会发现"我设的日志级别没生效"）。
    /// </para>
    /// </summary>
    private static LogEventLevel ReadConfiguredLogLevel(out string? warning)
    {
        warning = null;
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
        catch (Exception ex)
        {
            // 读到一半失败（文件被占用、内容损坏）= 无法确定用户想要的级别，退回 Debug 并把原因带出去。
            warning = $"{ex.GetType().Name}: {ex.Message}";
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
                // 只写日志的话，用户看到的是"界面照常、设置悄悄回到默认值"（输出目录、并发、语言全变）。
                // 这里把结论留给界面：DI 阶段还没有 ViewModel，InitializeAsync 起来后会把它报出来。
                Log.Warning(ex, "读取 settings.json 失败，改用默认配置");
                SettingsReadWarning ??= $"settings.json 读取失败（{ex.GetType().Name}），本次启动使用默认设置";
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
            catch (Exception ex)
            {
                // 静默 catch 的代价是"界面语言莫名其妙变回默认"，至少留下一条可搜索的记录。
                Log.Debug(ex, "读取界面语言失败，使用默认语言");
            }
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

