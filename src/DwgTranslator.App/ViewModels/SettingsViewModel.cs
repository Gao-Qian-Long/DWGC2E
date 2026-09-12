using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly string _settingsPath;
    private readonly ILocalizationService _localizationService;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _baseUrl = "https://api.deepseek.com";
    [ObservableProperty] private string _model = "deepseek-chat";
    [ObservableProperty] private string _autoCadPath = string.Empty;
    [ObservableProperty] private string _cadPluginPath = string.Empty;
    [ObservableProperty] private string _selectedLogLevel = "Debug";
    [ObservableProperty] private int _maxTranslationConcurrency = 12;
    [ObservableProperty] private string _resultText = string.Empty;
    [ObservableProperty] private Brush _resultBrush = Brushes.Gray;
    [ObservableProperty] private string _autoCadStatusText = string.Empty;
    [ObservableProperty] private Brush _autoCadStatusBrush = Brushes.Gray;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _isTestingCad;
    [ObservableProperty] private string _cadTestResultText = string.Empty;
    [ObservableProperty] private Brush _cadTestResultBrush = Brushes.Gray;

    public string[] LogLevels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    public int[] TranslationConcurrencyChoices { get; } = [4, 8, 12, 16, 20];

    public SettingsViewModel(ILocalizationService? localizationService = null)
    {
        _settingsPath = Path.Combine(App.AppDataDir, "settings.json");
        _localizationService = localizationService ?? new LocalizationService();
        LoadSettings();
        ApplyDetectedCadDefaults();
    }

    /// <summary>
    /// Loads settings from JSON config file and populates properties.
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;

            var json = File.ReadAllText(_settingsPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.ReadOptions) ?? new AppConfig();

            ApiKey = AppConfig.DecryptApiKey(config.DeepSeekApiKey);
            BaseUrl = config.DeepSeekBaseUrl ?? BaseUrl;
            Model = config.DeepSeekModel ?? Model;
            AutoCadPath = config.AutoCadInstallPath ?? string.Empty;
            CadPluginPath = config.CadPluginPath ?? string.Empty;
            MaxTranslationConcurrency = Math.Clamp(config.MaxTranslationConcurrency, 1, 20);

            if (!string.IsNullOrEmpty(config.MinimumLogLevel))
                SelectedLogLevel = config.MinimumLogLevel;

            UpdateAutoCadStatus();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings");
        }
    }

    /// <summary>
    /// Saves current properties to JSON config file. Returns true on success.
    /// </summary>
    public bool SaveSettings()
    {
        try
        {
            var config = File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_settingsPath), AppConfigJson.ReadOptions) ?? new AppConfig()
                : new AppConfig();

            config.DeepSeekApiKey = AppConfig.EncryptApiKey(ApiKey.Trim());
            config.DeepSeekBaseUrl = BaseUrl.Trim();
            config.DeepSeekModel = Model.Trim();
            config.AutoCadInstallPath = AutoCadPath.Trim();
            config.CadPluginPath = CadPluginPath.Trim();
            config.MinimumLogLevel = SelectedLogLevel;
            config.MaxTranslationConcurrency = Math.Clamp(MaxTranslationConcurrency, 1, 20);

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(config, AppConfigJson.WriteOptions));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings");
            MessageBox.Show(Strings.Get("SettingsSaveFailed"), Strings.Get("MsgTitleError"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Tests the DeepSeek API connection. Returns result text and brush color.
    /// </summary>
    public async Task TestConnectionAsync()
    {
        if (string.IsNullOrEmpty(ApiKey.Trim()))
        {
            ResultText = Strings.Get("SettingsTestEnterKey");
            ResultBrush = Brushes.Red;
            return;
        }

        IsTesting = true;
        ResultText = Strings.Get("SettingsTestConnecting");
        ResultBrush = Brushes.Gray;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl.Trim()) };
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey.Trim()}");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var client = new DeepSeekClient(httpClient, Model.Trim());
            var response = await Task.Run(
                () => client.ChatCompletionAsync("You are a test assistant.", "Say OK", cts.Token),
                cts.Token);

            ResultText = Strings.Get("SettingsTestSuccess", Truncate(response, 50));
            ResultBrush = Brushes.Green;
        }
        catch (OperationCanceledException)
        {
            ResultText = Strings.Get("SettingsTestTimeout");
            ResultBrush = Brushes.Red;
        }
        catch (HttpRequestException)
        {
            ResultText = Strings.Get("SettingsTestFailed", Strings.Get("ConnectionFailedNetwork"));
            ResultBrush = Brushes.Red;
        }
        catch (UriFormatException)
        {
            ResultText = Strings.Get("SettingsTestFailed", Strings.Get("ConnectionFailedFormat"));
            ResultBrush = Brushes.Red;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Connection test failed");
            ResultText = Strings.Get("SettingsTestFailed", Strings.Get("ConnectionFailedGeneric"));
            ResultBrush = Brushes.Red;
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// Runs AutoCAD detection and updates path/status.
    /// </summary>
    public async Task DetectAutoCadAsync()
    {
        AutoCadStatusText = Strings.Get("SettingsDetecting");
        AutoCadStatusBrush = Brushes.Gray;

        var result = await Task.Run(() => AutoCadDetector.DetectInstallation());

        if (result.Found)
        {
            AutoCadPath = result.InstallPath;
            AutoCadStatusText = Strings.Get("SettingsDetected", result.ProductName, result.Version, result.InstallPath);
            AutoCadStatusBrush = Brushes.Green;
        }
        else
        {
            AutoCadStatusText = Strings.Get("SettingsNotDetected");
            AutoCadStatusBrush = Brushes.Orange;
        }

        if (string.IsNullOrEmpty(CadPluginPath) || !File.Exists(CadPluginPath))
        {
            var pluginPath = AutoCadDetector.FindCadPlugin();
            if (pluginPath != null)
                CadPluginPath = pluginPath;
        }

        UpdateAutoCadStatus();
    }

    private void ApplyDetectedCadDefaults()
    {
        if (!AutoCadDetector.IsValidAutoCadPath(AutoCadPath))
        {
            var detected = AutoCadDetector.DetectInstallation();
            if (detected.Found)
                AutoCadPath = detected.InstallPath;
        }

        if (string.IsNullOrWhiteSpace(CadPluginPath) || !File.Exists(CadPluginPath))
            CadPluginPath = AutoCadDetector.FindCadPlugin() ?? string.Empty;

        UpdateAutoCadStatus();
    }

    /// <summary>
    /// Performs a real NETLOAD smoke test in the configured CAD executable.
    /// This validates the executable path, plugin path, CLR compatibility and
    /// private dependency closure instead of merely checking that files exist.
    /// </summary>
    public async Task TestCadIntegrationAsync()
    {
        ApplyDetectedCadDefaults();
        IsTestingCad = true;
        CadTestResultText = "正在启动CAD并验证插件…";
        CadTestResultBrush = Brushes.Gray;

        var scriptPath = Path.Combine(Path.GetTempPath(), $"dwgtranslator_cad_test_{Guid.NewGuid():N}.scr");
        var markerPath = Path.Combine(Path.GetTempPath(), "DwgTranslator", "plugin_ping.txt");
        Process? process = null;
        try
        {
            var exePath = ResolveCadExecutable(AutoCadPath);
            if (exePath == null)
                throw new InvalidOperationException("CAD安装路径中没有找到 gcad.exe 或 acad.exe。");
            if (string.IsNullOrWhiteSpace(CadPluginPath) || !File.Exists(CadPluginPath))
                throw new FileNotFoundException("没有找到随软件发布的CAD插件。", CadPluginPath);

            if (File.Exists(markerPath)) File.Delete(markerPath);
            var pluginForCad = CadPluginPath.Replace('\\', '/');
            File.WriteAllLines(scriptPath,
            [
                "_.NETLOAD",
                pluginForCad,
                "DWGTRANSLATORPING",
                "_.QUIT",
                "_N"
            ], Encoding.Default);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            process.StartInfo.ArgumentList.Add("/nologo");
            process.StartInfo.ArgumentList.Add("/b");
            process.StartInfo.ArgumentList.Add(scriptPath);

            if (!process.Start())
                throw new InvalidOperationException("CAD进程启动失败。");

            while (!File.Exists(markerPath) && !process.HasExited)
                await Task.Delay(250, cts.Token);

            if (!File.Exists(markerPath))
                throw new InvalidOperationException(
                    process.HasExited
                        ? $"CAD已退出且插件没有返回健康检查结果（退出码：{process.ExitCode}）。"
                        : "CAD已启动，但插件没有返回健康检查结果。");

            var marker = await File.ReadAllTextAsync(markerPath, cts.Token);
            if (!marker.StartsWith("status=ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"插件返回异常：{marker}");

            CadTestResultText = $"连接成功：{marker}";
            CadTestResultBrush = Brushes.Green;
        }
        catch (OperationCanceledException)
        {
            CadTestResultText = "验证超时，请关闭CAD弹窗后重试。";
            CadTestResultBrush = Brushes.Red;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CAD integration test failed");
            CadTestResultText = $"验证失败：{ex.Message}";
            CadTestResultBrush = Brushes.Red;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            process?.Dispose();
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            IsTestingCad = false;
        }
    }

    private static string? ResolveCadExecutable(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return null;
        foreach (var name in new[] { "gcad.exe", "acad.exe" })
        {
            var path = Path.Combine(installPath, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Updates AutoCAD path status indicator based on current path value.
    /// </summary>
    public void UpdateAutoCadStatus()
    {
        var path = AutoCadPath.Trim();
        if (string.IsNullOrEmpty(path))
        {
            AutoCadStatusText = Strings.Get("SettingsPathNotConfigured");
            AutoCadStatusBrush = Brushes.Gray;
        }
        else if (AutoCadDetector.IsValidAutoCadPath(path))
        {
            AutoCadStatusText = Strings.Get("SettingsPathValid");
            AutoCadStatusBrush = Brushes.Green;
        }
        else
        {
            AutoCadStatusText = Strings.Get("SettingsPathInvalid");
            AutoCadStatusBrush = Brushes.Red;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;
}
