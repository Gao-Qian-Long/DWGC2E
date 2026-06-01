using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DwgTranslator.App.Views;

public partial class SettingsDialog : Window
{
    private readonly string _settingsPath;
    private readonly ILocalizationService _localizationService;
    private string _apiKey = string.Empty;

    public SettingsDialog()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(App.AppDataDir, "settings.json");
        _localizationService = App.Services?.GetService<ILocalizationService>()
            ?? new LocalizationService();
        LoadSettings();
    }

    private void LoadSettings()
    {
        try
        {
            // Populate language ComboBox
            var languages = _localizationService.AvailableLanguages;
            foreach (var lang in languages)
                LanguageComboBox.Items.Add(lang);

            // Populate log level ComboBox
            var logLevels = new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };
            foreach (var level in logLevels)
                LogLevelComboBox.Items.Add(level);

            var currentLang = _localizationService.CurrentLanguage;
            string currentLogLevel = "Debug";

            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

                // Decrypt API key if stored with DPAPI protection
                var displayApiKey = AppConfig.DecryptApiKey(config.DeepSeekApiKey);

                _apiKey = displayApiKey;
                ApiKeyBox.Password = displayApiKey;
                BaseUrlBox.Text = config.DeepSeekBaseUrl;
                ModelBox.Text = config.DeepSeekModel;
                AutoCadPathBox.Text = config.AutoCadInstallPath;
                CadPluginPathBox.Text = config.CadPluginPath;

                // Use persisted language if available
                if (!string.IsNullOrEmpty(config.Language))
                    currentLang = config.Language;

                // Use persisted log level if available
                if (!string.IsNullOrEmpty(config.MinimumLogLevel))
                    currentLogLevel = config.MinimumLogLevel;
            }
            else
            {
                BaseUrlBox.Text = "https://api.deepseek.com";
                ModelBox.Text = "deepseek-chat";
            }

            // Select current language in ComboBox
            for (int i = 0; i < LanguageComboBox.Items.Count; i++)
            {
                if (LanguageComboBox.Items[i] is LanguageInfo info && info.CultureName == currentLang)
                {
                    LanguageComboBox.SelectedIndex = i;
                    break;
                }
            }
            if (LanguageComboBox.SelectedIndex < 0 && LanguageComboBox.Items.Count > 0)
                LanguageComboBox.SelectedIndex = 0;

            // Select current log level in ComboBox
            for (int i = 0; i < LogLevelComboBox.Items.Count; i++)
            {
                if (LogLevelComboBox.Items[i] is string level &&
                    string.Equals(level, currentLogLevel, StringComparison.OrdinalIgnoreCase))
                {
                    LogLevelComboBox.SelectedIndex = i;
                    break;
                }
            }
            if (LogLevelComboBox.SelectedIndex < 0)
                LogLevelComboBox.SelectedIndex = 1; // Default to "Debug"

            UpdateAutoCadStatus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex}");
            MessageBox.Show(Strings.Get("SettingsLoadFailed"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleApiKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (ApiKeyBox.Visibility == Visibility.Visible)
        {
            // Switch to visible TextBox: hide PasswordBox, show TextBox
            _apiKey = ApiKeyBox.Password;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyTextBox.Text = _apiKey;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Focus();
            ToggleApiKeyVisibility.Content = Strings.Get("BtnClose");
        }
        else
        {
            // Switch back to PasswordBox: hide TextBox, show PasswordBox
            _apiKey = ApiKeyTextBox.Text;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Password = _apiKey;
            ApiKeyBox.Visibility = Visibility.Visible;
            ToggleApiKeyVisibility.Content = Strings.Get("BtnShow");
        }
    }

    private string GetApiKey()
    {
        // Return from whichever control is currently visible
        if (ApiKeyTextBox.Visibility == Visibility.Visible)
            return ApiKeyTextBox.Text;

        try { return ApiKeyBox?.Password ?? _apiKey; } catch { return _apiKey; }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = GetApiKey().Trim();
        var baseUrl = BaseUrlBox.Text.Trim();
        var model = ModelBox.Text.Trim();

        if (string.IsNullOrEmpty(apiKey))
        {
            ResultText.Text = Strings.Get("SettingsTestEnterKey");
            ResultText.Foreground = Brushes.Red;
            return;
        }

        // Disable button to prevent double-click
        if (sender is Button testButton)
            testButton.IsEnabled = false;

        ResultText.Text = Strings.Get("SettingsTestConnecting");
        ResultText.Foreground = Brushes.Gray;

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var client = new DeepSeekClient(httpClient, model);
            var response = await System.Threading.Tasks.Task.Run(
                () => client.ChatCompletionAsync("You are a test assistant.", "Say OK", cts.Token),
                cts.Token);

            ResultText.Text = Strings.Get("SettingsTestSuccess", Truncate(response, 50));
            ResultText.Foreground = Brushes.Green;
        }
        catch (System.OperationCanceledException)
        {
            ResultText.Text = Strings.Get("SettingsTestTimeout");
            ResultText.Foreground = Brushes.Red;
        }
        catch (HttpRequestException)
        {
            ResultText.Text = Strings.Get("SettingsTestFailed", Strings.Get("ConnectionFailedNetwork"));
            ResultText.Foreground = Brushes.Red;
        }
        catch (UriFormatException)
        {
            ResultText.Text = Strings.Get("SettingsTestFailed", Strings.Get("ConnectionFailedFormat"));
            ResultText.Foreground = Brushes.Red;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Connection test failed: {ex}");
            ResultText.Text = Strings.Get("SettingsTestFailed", Strings.Get("ConnectionFailedGeneric"));
            ResultText.Foreground = Brushes.Red;
        }
        finally
        {
            if (sender is Button tb)
                tb.IsEnabled = true;
        }
    }

    private void BrowseAutoCad_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = BrowseForFolder(Strings.Get("SettingsBrowseAutoCad"), AutoCadPathBox.Text);
        if (selectedPath != null)
        {
            AutoCadPathBox.Text = selectedPath;
            UpdateAutoCadStatus();
        }
    }

    private async void DetectAutoCad_Click(object sender, RoutedEventArgs e)
    {
        AutoCadStatusText.Text = Strings.Get("SettingsDetecting");
        AutoCadStatusText.Foreground = Brushes.Gray;

        var result = await System.Threading.Tasks.Task.Run(() => AutoCadDetector.DetectInstallation());

        if (result.Found)
        {
            AutoCadPathBox.Text = result.InstallPath;
            AutoCadStatusText.Text = Strings.Get("SettingsDetected", result.ProductName, result.Version, result.InstallPath);
            AutoCadStatusText.Foreground = Brushes.Green;
        }
        else
        {
            AutoCadStatusText.Text = Strings.Get("SettingsNotDetected");
            AutoCadStatusText.Foreground = Brushes.Orange;
        }

        if (string.IsNullOrEmpty(CadPluginPathBox.Text))
        {
            var pluginPath = AutoCadDetector.FindCadPlugin();
            if (pluginPath != null)
            {
                CadPluginPathBox.Text = pluginPath;
            }
        }
    }

    private void BrowseCadPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Strings.Get("FilterDllFiles"),
            Title = Strings.Get("SettingsBrowsePlugin"),
            FileName = "DwgTranslator.Cad.dll"
        };

        string? initialDir = null;
        if (!string.IsNullOrEmpty(CadPluginPathBox.Text))
        {
            initialDir = Path.GetDirectoryName(CadPluginPathBox.Text);
        }
        if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var srcDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(srcDir, "DwgTranslator.Cad", "bin", config, "net8.0");
                if (Directory.Exists(candidate))
                {
                    initialDir = candidate;
                    break;
                }
            }
        }
        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
            dialog.InitialDirectory = initialDir;

        if (dialog.ShowDialog() == true)
        {
            CadPluginPathBox.Text = dialog.FileName;
            UpdateAutoCadStatus();
        }
    }

    private void UpdateAutoCadStatus()
    {
        var path = AutoCadPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            AutoCadStatusText.Text = Strings.Get("SettingsPathNotConfigured");
            AutoCadStatusText.Foreground = Brushes.Gray;
            return;
        }

        if (AutoCadDetector.IsValidAutoCadPath(path))
        {
            AutoCadStatusText.Text = Strings.Get("SettingsPathValid");
            AutoCadStatusText.Foreground = Brushes.Green;
        }
        else
        {
            AutoCadStatusText.Text = Strings.Get("SettingsPathInvalid");
            AutoCadStatusText.Foreground = Brushes.Red;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppConfig config;
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            else
            {
                config = new AppConfig();
            }

            // Encrypt API key at rest using DPAPI (CurrentUser scope)
            var plainApiKey = GetApiKey().Trim();
            config.DeepSeekApiKey = AppConfig.EncryptApiKey(plainApiKey);

            config.DeepSeekBaseUrl = BaseUrlBox.Text.Trim();
            config.DeepSeekModel = ModelBox.Text.Trim();
            config.AutoCadInstallPath = AutoCadPathBox.Text.Trim();
            config.CadPluginPath = CadPluginPathBox.Text.Trim();

            // Save log level selection
            if (LogLevelComboBox.SelectedItem is string selectedLogLevel)
                config.MinimumLogLevel = selectedLogLevel;

            // Save language selection
            if (LanguageComboBox.SelectedItem is LanguageInfo selectedLang)
            {
                config.Language = selectedLang.CultureName;
                // Apply language change for next launch
                try { _localizationService.SetLanguage(selectedLang.CultureName); } catch { }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(config, options));

            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex}");
            MessageBox.Show(Strings.Get("SettingsSaveFailed"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;

    private string? BrowseForFolder(string description, string? initialPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application", false);
            if (shellType == null) return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.BrowseForFolder(
                0, description, 0x00000010,
                0);

            if (folder == null) return null;

            dynamic folderItem = folder.Self;
            string path = folderItem.Path;

            return Directory.Exists(path) ? path : null;
        }
        catch
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = description,
                Filter = Strings.Get("FilterAutoCadExe"),
                FileName = "acad.exe"
            };

            if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                dialog.InitialDirectory = initialPath;

            if (dialog.ShowDialog() == true)
            {
                return Path.GetDirectoryName(dialog.FileName);
            }
            return null;
        }
    }
}
