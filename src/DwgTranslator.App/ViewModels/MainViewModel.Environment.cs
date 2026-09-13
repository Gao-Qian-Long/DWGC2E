using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Serilog;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// Delivery support: environment self-check and CAD plugin installation.
///
/// A delivered build has to answer three questions on the receiving machine without a developer
/// present: does this computer have everything the program needs, is the CAD integration in place,
/// and where do the logs and settings live when something does go wrong.
/// </summary>
public partial class MainViewModel
{
    private static readonly JsonSerializerOptions EnvironmentWriteOptions = AppConfigJson.WriteOptions;

    /// <summary>Folder holding the plugin that ships next to the application.</summary>
    public string CadPluginDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_config.CadPluginPath))
            {
                var dir = Path.GetDirectoryName(_config.CadPluginPath);
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, CadPluginInstaller.PluginFileName)))
                    return dir;
            }

            var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CadPlugin");
            return Directory.Exists(bundled) ? bundled : AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    /// <summary>
    /// CAD installation to install the plugin into, detected and remembered when unknown.
    /// </summary>
    public string ResolveCadInstallPath()
    {
        if (AutoCadDetector.IsValidAutoCadPath(_config.AutoCadInstallPath)) return _config.AutoCadInstallPath;

        try
        {
            var detection = AutoCadDetector.DetectInstallation();
            if (detection.Found && !string.IsNullOrWhiteSpace(detection.InstallPath))
            {
                _config.AutoCadInstallPath = detection.InstallPath!;
                PersistCadEnvironmentPaths();
                Log.Information("CAD installation detected and saved: {Path}", _config.AutoCadInstallPath);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "CAD detection failed"); }

        return _config.AutoCadInstallPath;
    }

    /// <summary>Masked API key state for the environment report; the key itself is never shown.</summary>
    public string ApiKeyStatusText => "由产品服务管理";

    public string TranslationServiceText => !_apiClient.IsConfigured ? "服务配置异常" : IsAccountLoggedIn ? "云端翻译服务 · 已登录" : "云端翻译服务 · 请登录后使用";

    public string AppDataDirectory => App.AppDataDir;
    public string LogDirectory => _config.LogDirectory;

    /// <summary>
    /// True when the program runs self-contained, so a receiving machine needs no .NET runtime.
    /// Reported by the self-check because "install the environment package" is the first question a
    /// user asks when handing the program to somebody else.
    /// </summary>
    public bool IsSelfContained
    {
        get
        {
            try
            {
                var runtimeConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "DwgTranslator") + ".runtimeconfig.json");
                return !File.Exists(runtimeConfig);
            }
            catch { return false; }
        }
    }

    [RelayCommand]
    private void ShowEnvironmentCheck() { SettingsSection = 3; CurrentPage = PageSettings; }

    /// <summary>Opens a folder in Explorer; used by the self-check for logs and settings.</summary>
    public void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "Opening {Path} failed", path); }
    }

    public void RevealPath(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else OpenFolder(Path.GetDirectoryName(path) ?? App.AppDataDir);
        }
        catch (Exception ex) { Log.Warning(ex, "Revealing {Path} failed", path); }
    }

    /// <summary>
    /// Saves only the CAD paths. The in-memory config holds the DECRYPTED API key, so the settings
    /// file is re-read and only these fields are written back.
    /// </summary>
    private void PersistCadEnvironmentPaths()
    {
        try
        {
            var path = _settingsPath ?? Path.Combine(App.AppDataDir, "settings.json");
            SettingsStore.Update(path, config => { config.AutoCadInstallPath = _config.AutoCadInstallPath; config.CadPluginPath = _config.CadPluginPath; });
            _settingsPath = path;
        }
        catch (Exception ex) { Log.Error(ex, "Saving CAD environment paths failed"); }
    }
}
