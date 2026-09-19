using DwgTranslator.App.Services;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Reflection;
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
            var existing = CadPluginSourceResolver.ResolveExisting(
                AppDomain.CurrentDomain.BaseDirectory, _config.CadPluginPath);
            if (existing != null) return Path.GetDirectoryName(Path.GetFullPath(existing))!;
            var extracted = Path.Combine(App.AppDataDir, "CadPlugin");
            try
            {
                ExtractEmbeddedCadPlugin(extracted);
                return extracted;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Embedded CAD plugin extraction failed");
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
    }

    private static void ExtractEmbeddedCadPlugin(string directory)
    {
        const string prefix = "DwgTranslator.App.Embedded.CadPlugin.";
        var assembly = typeof(MainViewModel).Assembly;
        var resources = assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var required in new[] { CadPluginInstaller.PluginFileName, "DwgTranslator.Core.dll", "cad-platform.txt" })
            if (!resources.Contains(prefix + required)) throw new FileNotFoundException(prefix + required);
        foreach (var resource in resources)
        {
            var destination = Path.Combine(directory, resource[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ExtractEmbedded(assembly, resource, destination);
        }
    }

    private static void ExtractEmbedded(Assembly assembly, string resource, string destination)
    {
        using var input = assembly.GetManifestResourceStream(resource) ?? throw new FileNotFoundException(resource);
        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            if (System.Security.Cryptography.SHA256.HashData(existing).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(input))) return;
            input.Position = 0;
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            SafeFileCommit.Commit(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
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
    public bool OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                ToastService.Warning("目录不存在或暂时不可访问，请检查路径后重试。");
                return false;
            }
            var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            if (process != null) return true;
            ToastService.Warning("无法打开文件资源管理器，请稍后重试。");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Path} failed", path);
            ToastService.Warning("无法打开目录，请检查权限或路径后重试。");
            return false;
        }
    }

    public bool RevealPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                if (process != null) return true;
                ToastService.Warning("无法打开文件资源管理器，请稍后重试。");
                return false;
            }
            return OpenFolder(Path.GetDirectoryName(path) ?? App.AppDataDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Revealing {Path} failed", path);
            ToastService.Warning("无法定位文件，请检查路径或权限后重试。");
            return false;
        }
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
