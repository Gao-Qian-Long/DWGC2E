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
    /// <summary>Clear only the rejected credential; never overwrite a newer login or other settings.</summary>
    public static bool ClearAuthenticationIfMatches(string path, string expectedEncryptedToken)
    {
        lock (Gate)
        {
            if (string.IsNullOrEmpty(expectedEncryptedToken) || !File.Exists(path)) return false;
            var current = Read(path);
            if (!string.Equals(current.AuthTokenEncrypted, expectedEncryptedToken, StringComparison.Ordinal)) return false;
            Update(path, c => c.AuthTokenEncrypted = string.Empty);
            return true;
        }
    }

    public static void Migrate(string path, AppConfig? deploymentDefaults = null)
    {
        lock (Gate)
        {
            var config = Read(path);
            if (config.ConfigurationVersion >= 1)
            {
                // Endpoint-only migration also applies to installations already on UI schema v1.
                if (DwgTranslator.Core.Api.ProductApiEndpoint.Migrate(config.ApiBaseUrl) != config.ApiBaseUrl)
                    Update(path, c => c.ApiBaseUrl = DwgTranslator.Core.Api.ProductApiEndpoint.Migrate(c.ApiBaseUrl));
                return;
            }
            if (File.Exists(path) && !File.Exists(path + ".pre-ui-v1.bak")) File.Copy(path, path + ".pre-ui-v1.bak", false);
            Update(path, c => {
                c.ApiMode = "worker";
                if (string.IsNullOrWhiteSpace(c.ApiBaseUrl)) c.ApiBaseUrl = (deploymentDefaults ?? new AppConfig()).ApiBaseUrl;
                c.ApiBaseUrl = DwgTranslator.Core.Api.ProductApiEndpoint.Migrate(c.ApiBaseUrl);
                c.ConfigurationVersion = 1;
            });
        }
    }
}
