using System.Security.Cryptography;
using System.Text;

namespace DwgTranslator.Core.Models;

/// <summary>
/// Application configuration loaded from settings.json.
/// </summary>
public class AppConfig
{
    /// <summary>
    /// Commercial licensing switch. Disabled for the current pre-commercial build;
    /// the existing licensing implementation remains available for a later release.
    /// </summary>
    public bool LicensingEnabled { get; set; } = false;

    public string DeepSeekApiKey { get; set; } = string.Empty;
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public string SourceLanguage { get; set; } = "ZH";
    public string TargetLanguage { get; set; } = "EN";
    public string GlossaryPath { get; set; } = "glossaries/mechanical_zh_en.json";
    public int BatchSize { get; set; } = 50;
    /// <summary>
    /// Maximum number of simultaneous translation requests. Translation is I/O-bound,
    /// so a value above the old fixed limit of 5 substantially improves large drawings.
    /// </summary>
    public int MaxTranslationConcurrency { get; set; } = 12;
    public int MaxRetryCount { get; set; } = 3;
    public double AutoScaleThreshold { get; set; } = 1.5;
    public double AutoScaleFactor { get; set; } = 0.95;
    public string ExportDirectory { get; set; } = "exports";
    public string LogDirectory { get; set; } = "logs";

    /// <summary>
    /// Minimum log level for file and UI output. Values: Verbose, Debug, Information, Warning, Error, Fatal.
    /// Default is "Debug" (all levels). Set to "Information" or "Warning" in production to reduce noise.
    /// </summary>
    public string MinimumLogLevel { get; set; } = "Debug";

    /// <summary>AutoCAD installation directory (e.g. C:\Program Files\Autodesk\AutoCAD 2026). Used for COM detection.</summary>
    public string AutoCadInstallPath { get; set; } = string.Empty;

    /// <summary>Path to DwgTranslator.Cad.dll plugin for NETLOAD. Empty = auto-detect.</summary>
    public string CadPluginPath { get; set; } = string.Empty;

    /// <summary>UI language culture code, e.g. "zh-CN" or "en-US". Default is "zh-CN".</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>
    /// Prefix used to identify DPAPI-encrypted API keys in settings.json.
    /// </summary>
    private const string DpapiPrefix = "DPAPI:";

    /// <summary>
    /// Decrypts a stored API key value. Handles both DPAPI-encrypted (prefixed with "DPAPI:")
    /// and legacy plaintext storage. Returns empty string on decryption failure.
    /// </summary>
    public static string DecryptApiKey(string storedKey)
    {
        if (string.IsNullOrEmpty(storedKey))
            return string.Empty;

        if (!storedKey.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return storedKey; // Legacy plaintext — return as-is

        try
        {
            var encrypted = Convert.FromBase64String(storedKey[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // Decryption failed (e.g., different user profile) — treat as unconfigured
            return string.Empty;
        }
    }

    /// <summary>
    /// Encrypts a plaintext API key for secure storage using DPAPI (CurrentUser scope).
    /// Returns the encrypted value prefixed with "DPAPI:".
    /// </summary>
    public static string EncryptApiKey(string plainKey)
    {
        if (string.IsNullOrEmpty(plainKey))
            return string.Empty;

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainKey), null, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Returns the decrypted API key from this config instance.
    /// </summary>
    public string GetDecryptedApiKey() => DecryptApiKey(DeepSeekApiKey);
}
