using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _viewModel;
    private string _apiKey = string.Empty;

    public SettingsDialog()
    {
        InitializeComponent();

        var localizationService = App.Services?.GetService<ILocalizationService>()
            ?? new LocalizationService();
        _viewModel = new SettingsViewModel(localizationService);
        DataContext = _viewModel;

        // PasswordBox doesn't support binding, so bridge manually
        ApiKeyBox.Password = _viewModel.ApiKey;
        _apiKey = _viewModel.ApiKey;

        // Sync TextBox fields (these don't use binding because they're in a dialog)
        BaseUrlBox.Text = _viewModel.BaseUrl;
        ModelBox.Text = _viewModel.Model;
        AutoCadPathBox.Text = _viewModel.AutoCadPath;
        CadPluginPathBox.Text = _viewModel.CadPluginPath;

        // Sync log level ComboBox
        LogLevelComboBox.ItemsSource = _viewModel.LogLevels;
        LogLevelComboBox.SelectedItem = _viewModel.SelectedLogLevel;
    }

    private void ToggleApiKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (ApiKeyBox.Visibility == Visibility.Visible)
        {
            _apiKey = ApiKeyBox.Password;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyTextBox.Text = _apiKey;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Focus();
            ToggleApiKeyVisibility.Content = Strings.Get("BtnClose");
        }
        else
        {
            _apiKey = ApiKeyTextBox.Text;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Password = _apiKey;
            ApiKeyBox.Visibility = Visibility.Visible;
            ToggleApiKeyVisibility.Content = Strings.Get("BtnShow");
        }
    }

    private string GetApiKey()
    {
        if (ApiKeyTextBox.Visibility == Visibility.Visible)
            return ApiKeyTextBox.Text;
        try { return ApiKeyBox?.Password ?? _apiKey; } catch { return _apiKey; }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.IsEnabled = false;
        _viewModel.ApiKey = GetApiKey();
        await _viewModel.TestConnectionAsync();
        if (sender is Button tb) tb.IsEnabled = true;
    }

    private void BrowseAutoCad_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = BrowseForFolder(Strings.Get("SettingsBrowseAutoCad"), _viewModel.AutoCadPath);
        if (selectedPath != null)
        {
            _viewModel.AutoCadPath = selectedPath;
            _viewModel.UpdateAutoCadStatus();
        }
    }

    private async void DetectAutoCad_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DetectAutoCadAsync();
    }

    private async void TestCadIntegration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        await _viewModel.TestCadIntegrationAsync();
        if (sender is Button completedButton) completedButton.IsEnabled = true;
    }

    private void BrowseCadPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("FilterDllFiles"),
            Title = Strings.Get("SettingsBrowsePlugin"),
            FileName = "DwgTranslator.Cad.dll"
        };

        string? initialDir = null;
        if (!string.IsNullOrEmpty(_viewModel.CadPluginPath))
            initialDir = Path.GetDirectoryName(_viewModel.CadPluginPath);

        if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var srcDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));
            foreach (var config in new[] { "Debug", "Release" })
            {
                foreach (var framework in new[] { "net48", "net8.0" })
                {
                    var candidate = Path.Combine(srcDir, "DwgTranslator.Cad", "bin", config, framework);
                    if (Directory.Exists(candidate)) { initialDir = candidate; break; }
                }
                if (!string.IsNullOrEmpty(initialDir)) break;
            }
        }

        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
            dialog.InitialDirectory = initialDir;

        if (dialog.ShowDialog() == true)
        {
            _viewModel.CadPluginPath = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ApiKey = GetApiKey();
        _viewModel.BaseUrl = BaseUrlBox.Text;
        _viewModel.Model = ModelBox.Text;
        _viewModel.AutoCadPath = AutoCadPathBox.Text;
        _viewModel.CadPluginPath = CadPluginPathBox.Text;
        if (LogLevelComboBox.SelectedItem is string logLevel)
            _viewModel.SelectedLogLevel = logLevel;

        if (_viewModel.SaveSettings())
            DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private string? BrowseForFolder(string description, string? initialPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application", false);
            if (shellType == null) return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.BrowseForFolder(0, description, 0x00000010, 0);
            if (folder == null) return null;

            dynamic folderItem = folder.Self;
            string path = folderItem.Path;
            return Directory.Exists(path) ? path : null;
        }
        catch
        {
            var dialog = new OpenFileDialog
            {
                Title = description,
                Filter = Strings.Get("FilterAutoCadExe"),
                FileName = "acad.exe"
            };

            if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                dialog.InitialDirectory = initialPath;

            return dialog.ShowDialog() == true ? Path.GetDirectoryName(dialog.FileName) : null;
        }
    }
}
