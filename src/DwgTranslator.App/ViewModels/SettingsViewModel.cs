using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly string _settingsPath;
    private readonly ILocalizationService _localizationService;
    private static readonly JsonSerializerOptions s_writeIndentedOptions = new() { WriteIndented = true };

    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _baseUrl = "https://api.deepseek.com";
    [ObservableProperty] private string _model = "deepseek-chat";
    [ObservableProperty] private string _autoCadPath = string.Empty;
    [ObservableProperty] private string _cadPluginPath = string.Empty;
    [ObservableProperty] private string _selectedLogLevel = "Debug";
    [ObservableProperty] private string _resultText = string.Empty;
    [ObservableProperty] private Brush _resultBrush = Brushes.Gray;
    [ObservableProperty] private string _autoCadStatusText = string.Empty;
    [ObservableProperty] private Brush _autoCadStatusBrush = Brushes.Gray;
    [ObservableProperty] private bool _isTesting;

    public string[] LogLevels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    public SettingsViewModel(ILocalizationService? localizationService = null)
    {
        _settingsPath = Path.Combine(App.AppDataDir, "settings.json");
        _localizationService = localizationService ?? new LocalizationService();
        LoadSettings();
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
            var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

            ApiKey = AppConfig.DecryptApiKey(config.DeepSeekApiKey);
            BaseUrl = config.DeepSeekBaseUrl ?? BaseUrl;
            Model = config.DeepSeekModel ?? Model;
            AutoCadPath = config.AutoCadInstallPath ?? string.Empty;
            CadPluginPath = config.CadPluginPath ?? string.Empty;

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
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_settingsPath)) ?? new AppConfig()
                : new AppConfig();

            config.DeepSeekApiKey = AppConfig.EncryptApiKey(ApiKey.Trim());
            config.DeepSeekBaseUrl = BaseUrl.Trim();
            config.DeepSeekModel = Model.Trim();
            config.AutoCadInstallPath = AutoCadPath.Trim();
            config.CadPluginPath = CadPluginPath.Trim();
            config.MinimumLogLevel = SelectedLogLevel;

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(config, s_writeIndentedOptions));
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

        if (string.IsNullOrEmpty(CadPluginPath))
        {
            var pluginPath = AutoCadDetector.FindCadPlugin();
            if (pluginPath != null)
                CadPluginPath = pluginPath;
        }
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
