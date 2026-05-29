using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace DwgTranslator.App.Views;

public partial class SettingsDialog : Window
{
    private readonly string _settingsPath;

    public SettingsDialog()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(App.AppDataDir, "settings.json");
        LoadSettings();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                ApiKeyBox.Text = config.DeepSeekApiKey;
                BaseUrlBox.Text = config.DeepSeekBaseUrl;
                ModelBox.Text = config.DeepSeekModel;
            }
            else
            {
                BaseUrlBox.Text = "https://api.deepseek.com";
                ModelBox.Text = "deepseek-chat";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Text.Trim();
        var baseUrl = BaseUrlBox.Text.Trim();
        var model = ModelBox.Text.Trim();

        if (string.IsNullOrEmpty(apiKey))
        {
            ResultText.Text = "请先填写 API Key";
            ResultText.Foreground = Brushes.Red;
            return;
        }

        ResultText.Text = "正在测试...";
        ResultText.Foreground = Brushes.Gray;

        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var client = new DeepSeekClient(httpClient, model);
            var response = await client.ChatCompletionAsync("You are a test assistant.", "Say OK");

            ResultText.Text = $"连接成功! 模型响应: {Truncate(response, 50)}";
            ResultText.Foreground = Brushes.Green;
        }
        catch (Exception ex)
        {
            ResultText.Text = $"连接失败: {Truncate(ex.Message, 80)}";
            ResultText.Foreground = Brushes.Red;
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

            config.DeepSeekApiKey = ApiKeyBox.Text.Trim();
            config.DeepSeekBaseUrl = BaseUrlBox.Text.Trim();
            config.DeepSeekModel = ModelBox.Text.Trim();

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(config, options));

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;
}
