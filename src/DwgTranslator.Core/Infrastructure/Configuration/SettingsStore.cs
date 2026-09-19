using System.Text.Json;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>Serializes in-process configuration updates; callers change only fields they own.</summary>
public static class SettingsStore
{
    private static readonly object Gate = new();
    /// <summary>
    /// Keys that must never survive a load/save round-trip. They land in
    /// <see cref="AppConfig.AdditionalSettings"/> ([JsonExtensionData]) because no property owns them,
    /// so without an explicit sweep an upgraded install keeps them in settings.json forever.
    /// Two groups: retired AI provider credentials, and retired switches that no longer have any
    /// behaviour but would still be read as "the user asked for this" by an older build.
    /// </summary>
    private static readonly HashSet<string> RemovedSettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DeepSeekApiKey", "DeepSeekBaseUrl", "DeepSeekModel", "ApiMode",
        // The output resolver hard-refuses to target the source drawing; the old switch is gone
        // from the UI and from every read path, so the value must not be preserved either.
        "AllowOverwriteSource"
    };

    public static AppConfig Read(string path)
    {
        lock (Gate)
        {
            // 文件不存在 = 全新安装，默认值就是正确答案。文件存在但反序列化成 null（字面量 null、
            // 标量文档、缺字段的空对象）则是"读不到用户数据"——旧实现静默回退默认值，紧接着 Update
            // 会把这份默认值整份写回：登录令牌、账号 ID、输出目录在用户毫不知情时被清空。这里改成
            // 明确失败，磁盘上的原文件一个字节都不动。
            if (!File.Exists(path)) return new AppConfig();

            var config = JsonSerializer.Deserialize<AppConfig?>(File.ReadAllText(path), AppConfigJson.ReadOptions);
            if (config == null)
                throw new InvalidDataException(
                    $"设置文件无法解析为配置对象，已保留原文件不做修改，请检查后重试：{path}");
            RemoveRemovedSettings(config);
            return config;
        }
    }

    public static void RemoveRemovedSettings(AppConfig config)
    {
        if (config.AdditionalSettings == null) return;
        foreach (var key in config.AdditionalSettings.Keys.Where(RemovedSettingNames.Contains).ToList())
            config.AdditionalSettings.Remove(key);
        if (config.AdditionalSettings.Count == 0) config.AdditionalSettings = null;
    }

    public static AppConfig Update(string path, Action<AppConfig> change)
    {
        lock (Gate)
        {
            var config = Read(path);
            change(config);
            RemoveRemovedSettings(config);
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
            // Inspect the raw document before Read() sanitizes JsonExtensionData. Otherwise a
            // schema-2 file still carrying a removed key would never be rewritten.
            var config = Read(path);
            var rawContainsRemovedSettings = File.Exists(path) && RawJsonContainsRemovedSettings(path);
            var endpoint = DwgTranslator.Core.Api.ProductApiEndpoint.Migrate(config.ApiBaseUrl);
            var needsRewrite = config.ConfigurationVersion < 2
                || !string.Equals(endpoint, config.ApiBaseUrl, StringComparison.Ordinal)
                || rawContainsRemovedSettings;
            if (!needsRewrite) return;

            if (File.Exists(path) && config.ConfigurationVersion < 1 && !File.Exists(path + ".pre-ui-v1.bak"))
                File.Copy(path, path + ".pre-ui-v1.bak", false);

            Update(path, c =>
            {
                if (string.IsNullOrWhiteSpace(c.ApiBaseUrl))
                    c.ApiBaseUrl = (deploymentDefaults ?? new AppConfig()).ApiBaseUrl;
                c.ApiBaseUrl = DwgTranslator.Core.Api.ProductApiEndpoint.Migrate(c.ApiBaseUrl);
                RemoveRemovedSettings(c);
                c.ConfigurationVersion = Math.Max(c.ConfigurationVersion, 2);
            });
        }
    }

    private static bool RawJsonContainsRemovedSettings(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(property => RemovedSettingNames.Contains(property.Name));
    }
}