using DwgTranslator.App.Services;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
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

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled UI exception");
        // Graceful degradation: show non-blocking warning instead of crashing
        try
        {
            MessageBox.Show(
                $"程序遇到一个错误，但已自动恢复。\n\n错误: {e.Exception.Message}\n\n如果问题持续出现，请查看日志或联系技术支持。",
                "运行警告", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    $"程序遇到致命错误即将关闭。\n\n{ex?.Message}\n\n日志位置: {Path.Combine(AppDataDir, "logs")}",
                    "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // Configure Serilog
        var logPath = Path.Combine(AppDataDir, "logs", "dwgtranslator-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Application started. App data: {Dir}", AppDataDir);

        // Initialize license service
        LicenseService = new LicenseService(AppDataDir);
        LicenseService.LoadLicense();
        Log.Information("License status: {Status}", LicenseService.CurrentLicense.GetDisplayStatus());

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
                        "欢迎使用 DWG Translator v2.1!\n\n" +
                        "首次使用需要配置 DeepSeek API Key。\n" +
                        "请点击「设置」按钮进行配置。\n\n" +
                        "体验版提供 3 次免费翻译导出，可随时激活升级。",
                        "首次配置",
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

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core services (concrete types map 1:1 to their interfaces)
        services.AddSingleton<IGlossaryService, GlossaryService>();
        services.AddSingleton<IExcelService, ExcelService>();
        services.AddSingleton<IDwgReaderService, DwgReaderService>();
        services.AddSingleton<IDwgWriterService, DwgWriterService>();
        services.AddSingleton<IAutoCadInteropService, AutoCadInteropService>();

        // LicenseService needs the AppDataDir parameter
        services.AddSingleton<ILicenseService>(sp => LicenseService);

        // Translation helpers (stateless, safe to share)
        services.AddSingleton<FormatCodeParser>();

        // ViewModel
        services.AddTransient<MainViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application shutting down");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
