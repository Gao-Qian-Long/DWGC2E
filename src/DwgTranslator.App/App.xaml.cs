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
                var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(json);
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
                var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(json);
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
            var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(json);
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
                    var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(json);
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
