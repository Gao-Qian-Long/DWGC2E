using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DwgTranslator.Core.Api;

// ─────────────────────────────────────────────────────────────────────────────
// 客户端与后端（Cloudflare Worker + D1）之间的唯一契约。
//
// 设计约束（来自重构基线）：
//   · UI 层与 CAD 插件都不得直接知道 DeepSeek / 模型名 / System Prompt；
//     它们只依赖 IApiClient。
//   · 翻译请求必须是结构化的（source_lang / target_lang / items[]），
//     而不是一个自由 prompt——规则与模型由服务端掌控。
//   · 后端未就绪时，客户端仍能用"直连模式"跑通全部流程；切到 Worker 只改配置。
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TranslationItem
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    /// <summary>字高（图纸单位），服务端据此判断排版策略。</summary>
    public double Height { get; set; }
    /// <summary>旋转角度（度）。</summary>
    public double Rotation { get; set; }
    public string Layer { get; set; } = string.Empty;
    /// <summary>块/属性等上下文，供服务端做工程语义判断。</summary>
    public string? Context { get; set; }
}

public sealed class TranslationBatchRequest
{
    /// <summary>Reuse for transport retries of the same immutable batch.</summary>
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceLang { get; set; } = string.Empty;
    public string TargetLang { get; set; } = string.Empty;
    /// <summary>术语表条目（服务端可覆盖，客户端只作提示）。</summary>
    public List<GlossaryHint> Glossary { get; set; } = new();
    public List<TranslationItem> Items { get; set; } = new();

    /// <summary>客户端保护规则开关（尺寸 / 公差 / 型号 / 术语优先）。</summary>
    public ProtectionFlags Protection { get; set; } = new();
}

public sealed class GlossaryHint
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
}

public sealed class ProtectionFlags
{
    public bool ProtectDimensions { get; set; } = true;
    public bool ProtectTolerances { get; set; } = true;
    public bool ProtectModels { get; set; } = true;
    public bool GlossaryFirst { get; set; } = true;
}

public sealed class TranslationBatchResult
{
    public bool Success { get; set; }
    public List<TranslationItemResult> Items { get; set; } = new();
    /// <summary>本次消耗的字符数（服务端计费用；直连模式由客户端估算）。</summary>
    public int CharactersUsed { get; set; }
    public int CachedCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}

public sealed class TranslationItemResult
{
    public int Id { get; set; }
    public string? TranslatedText { get; set; }
    public bool FromCache { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class CloudGlossaryEntry
{
    public string? Id { get; set; }
    public string? Note { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class LoginResult
{
    public string? UserId { get; set; }
    public bool Success { get; set; }
    public string? Token { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}

public sealed class ProfileInfo
{
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class SubscriptionInfo
{
    public string PlanName { get; set; } = string.Empty;
    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool AutoRenew { get; set; }
    public List<string> Entitlements { get; set; } = new();
}

public sealed class UsageInfo
{
    public long MonthlyQuota { get; set; }
    public long Used { get; set; }
    public long Remaining => Math.Max(0, MonthlyQuota - Used);
    public DateTime? ResetAt { get; set; }
}

public sealed class DeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = "Windows";
    public DateTime? LastSeenAt { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class DeviceBindResult
{
    public bool Success { get; set; }
    public int UsedDevices { get; set; }
    public int MaxDevices { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}

public sealed class VersionInfo
{
    public string LatestVersion { get; set; } = string.Empty;
    public string? DownloadUrl { get; set; }
    public string? BackupDownloadUrl { get; set; }
    public string? ReleaseNotes { get; set; }
    public bool Mandatory { get; set; }
}

/// <summary>
/// 客户端唯一的后端入口。两种实现：
///   · WorkerApiClient —— 走 Cloudflare Worker（正式形态，见 docs/CF_BACKEND_CONTRACT.md）
///   · DirectApiClient  —— 直连模式（过渡形态：客户端持 Key，仅内测用）
/// UI 与 CAD 插件只认这个接口，切换后端不改上层代码。
/// </summary>
/// <summary>表示本地保存的登录会话已经被 Worker 拒绝，需要清除会话并重新登录。</summary>
public sealed class ApiAuthenticationException : Exception
{
    public string ErrorCode { get; }

    public ApiAuthenticationException(string errorCode, string? message = null)
        : base(message ?? ApiErrorMessages.Describe(errorCode))
    {
        ErrorCode = errorCode;
    }
}

public interface IApiClient
{
    /// <summary>当前实现是否具备可用配置（Worker 模式要求 BaseUrl；直连模式要求 Key）。</summary>
    bool IsConfigured { get; }

    /// <summary>模式名，用于界面显示与排查（worker / direct）。</summary>
    string ModeName { get; }

    Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default);

    Task<ProfileInfo?> GetProfileAsync(CancellationToken cancellationToken = default);

    Task<SubscriptionInfo?> GetSubscriptionAsync(CancellationToken cancellationToken = default);

    Task<UsageInfo?> GetUsageAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceInfo>?> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<TranslationBatchResult> TranslateAsync(TranslationBatchRequest request, CancellationToken cancellationToken = default);

    Task<DeviceBindResult> BindDeviceAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default);

    /// <summary>撤销当前账号的一台设备及其会话。</summary>
    Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    Task<VersionInfo?> CheckVersionAsync(string currentVersion, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudGlossaryEntry>?> GetGlossaryAsync(CancellationToken cancellationToken = default);

    Task<bool> PutGlossaryAsync(IReadOnlyList<CloudGlossaryEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>
/// 后端返回的错误码 → 用户能看懂的中文提示。
/// 提示词要求"错误提示必须专业"，不允许所有错误都显示"操作失败"。
/// </summary>
public static class ApiErrorMessages
{
    public static string Describe(string? errorCode, string? fallback = null)
    {
        return errorCode switch
        {
            "unauthenticated" or "token_expired" => "登录已过期，请重新登录",
            "subscription_expired" => "会员已到期",
            "quota_exceeded" => "本月翻译额度不足",
            "device_limit" => "设备数量已达到套餐上限",
            "device_owned_by_other_account" => "该设备已绑定其他账号",
            "device_not_found" => "设备不存在或已经移除",
            "network_error" => "网络连接失败，正在重试",
            "upstream_unavailable" => "翻译服务暂时不可用，请稍后再试",
            "invalid_drawing" => "无法解析该 DWG 文件",
            "file_locked" => "文件正在被其他程序占用",
            "output_exists" => "输出文件已存在",
            "cad_version_unsupported" => "当前 CAD 版本暂不支持",
            "rate_limited" => "请求过于频繁，正在排队重试",
            "glossary_limit" => "云端术语库最多保存 1000 条",
            _ => fallback ?? "操作失败"
        };
    }
}

