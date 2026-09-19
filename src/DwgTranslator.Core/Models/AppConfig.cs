using System.Security.Cryptography;
using System.Text;

namespace DwgTranslator.Core.Models;

/// <summary>
/// Application configuration loaded from settings.json.
/// </summary>
public class AppConfig
{
    public int ConfigurationVersion { get; set; }

    /// <summary>Round-trip fields introduced by newer clients without interpreting them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalSettings { get; set; }
    /// <summary>
    /// Commercial licensing switch. Disabled for the current pre-commercial build;
    /// the existing licensing implementation remains available for a later release.
    /// </summary>
    public bool LicensingEnabled { get => false; set { /* Legacy setting: cloud accounts are authoritative. */ } }

#if DEBUG
    // Debug-only compatibility shims for migration regression tests. Production clients are Worker-only.
    [System.Text.Json.Serialization.JsonIgnore, Obsolete("AI provider credentials are managed by the Worker.")]
    public string DeepSeekApiKey { get => string.Empty; set { } }
    [System.Text.Json.Serialization.JsonIgnore, Obsolete("AI provider endpoints are managed by the Worker.")]
    public string DeepSeekBaseUrl { get => string.Empty; set { } }
    [System.Text.Json.Serialization.JsonIgnore, Obsolete("AI model selection is managed by the Worker.")]
    public string DeepSeekModel { get => string.Empty; set { } }
#endif
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
    public string ExportDirectory { get; set; } = string.Empty;
    public Dictionary<string, string> AccountOutputDirectories { get; set; } = new();
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
#if DEBUG
    [Obsolete("AI provider credentials are managed by the Worker.")]
    public string GetDecryptedApiKey() => string.Empty;
#endif

    // ── 任务层与并发（本地并发与 AI 并发分开限流）──
    public int LocalWorkerCount { get; set; } = 2;
    public int AiConcurrency { get; set; } = 4;
    public bool MemoryOptimization { get; set; } = true;

    // ── 后端对接（客户端只认 IApiClient）──
#if DEBUG
    [System.Text.Json.Serialization.JsonIgnore, Obsolete("The production client always uses the Worker.")]
    public string ApiMode { get => "worker"; set { } }
#endif
    public string ApiBaseUrl { get; set; } = DwgTranslator.Core.Api.ProductApiEndpoint.Default;
    public string AuthTokenEncrypted { get; set; } = string.Empty;
    public string ActiveAccountId { get; set; } = string.Empty;
    public string UpdateManifestUrl { get; set; } = "https://cad.pocketter.dpdns.org/update/latest.json";
    public bool AutoCheckUpdate { get; set; } = true;

    // ── 文件安全（默认不覆盖原文件）──
    public string OutputNamingPattern { get; set; } = "{name}_{lang}";
    public string DuplicatePolicy { get; set; } = "rename";
    public bool BackupSourceBeforeWrite { get; set; } = false;

    // ── 工程保护规则开关（直接作用于过滤与校验）──
    public bool ProtectDimensions { get; set; } = true;
    public bool ProtectTolerances { get; set; } = true;
    public bool ProtectModels { get; set; } = true;
    public bool GlossaryFirst { get; set; } = true;

    // ── 常规 ──
    public bool StartWithWindows { get; set; } = false;
    public bool OpenOutputFolderAfterExport { get; set; } = true;

    /// <summary>
    /// 启动时恢复上次的工作区（语言对 + 上次打开的图纸列表）。默认开启；关掉后每次启动都是空工作区。
    /// 记录本身是便利缓存（<c>workspace-session.json</c>），读不出来只退化成空工作区，不影响其它功能。
    /// </summary>
    public bool RestoreLastWorkspace { get; set; } = true;
}

