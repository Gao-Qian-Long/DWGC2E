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
            var json = File.ReadAllText(settingsPath);
            var config = System.Text.Json.JsonSerializer.Deserialize<DwgTranslator.Core.Models.AppConfig>(json);
            if (config != null && string.IsNullOrEmpty(config.DeepSeekApiKey))
            {
                var result = MessageBox.Show(
                    "欢迎使用 DWG Translator!\n\n" +
                    "首次使用需要配置 DeepSeek API Key。\n" +
                    "请在设置文件中填入您的 API Key：\n\n" +
                    settingsPath,
                    "首次配置",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application shutting down");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
