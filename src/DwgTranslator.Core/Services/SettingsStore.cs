using System.Text.Json;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>Serializes in-process configuration updates; callers change only fields they own.</summary>
public static class SettingsStore
{
    private static readonly object Gate = new();
    public static AppConfig Read(string path)
    {
        lock (Gate) return File.Exists(path) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), AppConfigJson.ReadOptions) ?? new() : new();
    }
    public static AppConfig Update(string path, Action<AppConfig> change)
    {
        lock (Gate)
        {
            var config = Read(path);
            change(config);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(config, AppConfigJson.WriteOptions));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return config;
        }
    }
    public static void Migrate(string path, AppConfig? deploymentDefaults = null)
    {
        lock (Gate)
        {
            var config = Read(path);
            if (config.ConfigurationVersion >= 1) return;
            if (File.Exists(path) && !File.Exists(path + ".pre-ui-v1.bak")) File.Copy(path, path + ".pre-ui-v1.bak", false);
            Update(path, c => {
                c.ApiMode = "worker";
                if (string.IsNullOrWhiteSpace(c.ApiBaseUrl)) c.ApiBaseUrl = (deploymentDefaults ?? new AppConfig()).ApiBaseUrl;
                c.ConfigurationVersion = 1;
            });
        }
    }
}
