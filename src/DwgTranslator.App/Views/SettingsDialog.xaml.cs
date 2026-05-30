using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
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
    private string _apiKey = string.Empty;

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
                _apiKey = config.DeepSeekApiKey;
                ApiKeyBox.Password = config.DeepSeekApiKey;
                BaseUrlBox.Text = config.DeepSeekBaseUrl;
                ModelBox.Text = config.DeepSeekModel;
                AutoCadPathBox.Text = config.AutoCadInstallPath;
                CadPluginPathBox.Text = config.CadPluginPath;
            }
            else
            {
                BaseUrlBox.Text = "https://api.deepseek.com";
                ModelBox.Text = "deepseek-chat";
            }

            UpdateAutoCadStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleApiKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        // Simply toggle between showing and hiding the password
        if (ApiKeyBox.Visibility == Visibility.Visible)
        {
            // Switch to visible TextBox
            var parent = ApiKeyBox.Parent as StackPanel;
            if (parent == null) return;

            int index = parent.Children.IndexOf(ApiKeyBox);
            parent.Children.RemoveAt(index);

            var textBox = new TextBox
            {
                Name = "ApiKeyTextBox",
                Width = 300,
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = _apiKey,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0"))
            };
            textBox.TextChanged += (s, args) => _apiKey = textBox.Text;

            parent.Children.Insert(index, textBox);
            ToggleApiKeyVisibility.Content = "隐藏";
        }
    }

    private string GetApiKey()
    {
        // Check for TextBox replacement first
        var parent = ApiKeyBox?.Parent as StackPanel;
        if (parent != null)
        {
            foreach (var child in parent.Children)
            {
                if (child is TextBox tb && tb.Name == "ApiKeyTextBox")
                    return tb.Text;
            }
        }

        // Fall back to PasswordBox
        try { return ApiKeyBox?.Password ?? _apiKey; } catch { return _apiKey; }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = GetApiKey().Trim();
        var baseUrl = BaseUrlBox.Text.Trim();
        var model = ModelBox.Text.Trim();

        if (string.IsNullOrEmpty(apiKey))
        {
            ResultText.Text = "请先填写 API Key";
            ResultText.Foreground = Brushes.Red;
            return;
        }

        // Disable button to prevent double-click
        if (sender is Button testButton)
            testButton.IsEnabled = false;

        ResultText.Text = "正在测试连接...";
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

            ResultText.Text = $"连接成功! 模型响应: {Truncate(response, 50)}";
            ResultText.Foreground = Brushes.Green;
        }
        catch (System.OperationCanceledException)
        {
            ResultText.Text = "连接超时（15秒）";
            ResultText.Foreground = Brushes.Red;
        }
        catch (Exception ex)
        {
            ResultText.Text = $"连接失败: {Truncate(ex.Message, 80)}";
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
        var selectedPath = BrowseForFolder("选择 AutoCAD 安装目录（包含 acad.exe 的文件夹）", AutoCadPathBox.Text);
        if (selectedPath != null)
        {
            AutoCadPathBox.Text = selectedPath;
            UpdateAutoCadStatus();
        }
    }

    private async void DetectAutoCad_Click(object sender, RoutedEventArgs e)
    {
        AutoCadStatusText.Text = "正在检测 AutoCAD 安装...";
        AutoCadStatusText.Foreground = Brushes.Gray;

        var result = await System.Threading.Tasks.Task.Run(() => AutoCadDetector.DetectInstallation());

        if (result.Found)
        {
            AutoCadPathBox.Text = result.InstallPath;
            AutoCadStatusText.Text = $"已检测到: {result.ProductName} ({result.Version}) — {result.InstallPath}";
            AutoCadStatusText.Foreground = Brushes.Green;
        }
        else
        {
            AutoCadStatusText.Text = "未检测到 AutoCAD 安装。请手动选择安装目录，或确认 AutoCAD 已安装。";
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
            Filter = "DLL 文件|*.dll|所有文件|*.*",
            Title = "选择 DwgTranslator.Cad.dll 插件文件",
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
            AutoCadStatusText.Text = "未配置 AutoCAD 路径 — 精确回写模式不可用，将使用离线回写";
            AutoCadStatusText.Foreground = Brushes.Gray;
            return;
        }

        if (AutoCadDetector.IsValidAutoCadPath(path))
        {
            AutoCadStatusText.Text = "AutoCAD 路径有效 (已找到 acad.exe)";
            AutoCadStatusText.Foreground = Brushes.Green;
        }
        else
        {
            AutoCadStatusText.Text = "路径无效 — 未找到 acad.exe，请确认是否为 AutoCAD 安装目录";
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

            config.DeepSeekApiKey = GetApiKey().Trim();
            config.DeepSeekBaseUrl = BaseUrlBox.Text.Trim();
            config.DeepSeekModel = ModelBox.Text.Trim();
            config.AutoCadInstallPath = AutoCadPathBox.Text.Trim();
            config.CadPluginPath = CadPluginPathBox.Text.Trim();

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
                Filter = "AutoCAD|acad.exe|所有文件|*.*",
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
