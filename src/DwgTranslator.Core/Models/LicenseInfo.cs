namespace DwgTranslator.Core.Models;

/// <summary>
/// License type enumeration.
/// </summary>
public enum LicenseType
{
    None,
    Trial,        // 3 free uses, no activation code needed
    Perpetual,    // Buy-out / lifetime — ¥699 one-time
    Subscription  // Monthly ¥49 / yearly ¥399
}

/// <summary>
/// License status enumeration.
/// </summary>
public enum LicenseStatus
{
    Valid,
    Expired,
    Invalid,
    TrialExhausted,
    NotActivated
}

/// <summary>
/// Represents the application's license information.
/// </summary>
public class LicenseInfo
{
    public LicenseType Type { get; set; }
    public string? MachineId { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? FirstUseDate { get; set; }
    public int TrialUsesRemaining { get; set; }
    public string? ActivationCode { get; set; }

    public LicenseStatus GetStatus()
    {
        if (Type == LicenseType.Perpetual)
            return LicenseStatus.Valid;

        if (Type == LicenseType.Subscription)
        {
            if (ExpiryDate.HasValue && ExpiryDate.Value > DateTime.UtcNow)
                return LicenseStatus.Valid;
            return LicenseStatus.Expired;
        }

        if (Type == LicenseType.Trial)
        {
            if (TrialUsesRemaining > 0)
                return LicenseStatus.Valid;
            return LicenseStatus.TrialExhausted;
        }

        return LicenseStatus.NotActivated;
    }

    public bool IsValid => GetStatus() == LicenseStatus.Valid;

    public string GetDisplayStatus()
    {
        return GetStatus() switch
        {
            LicenseStatus.Valid => Type == LicenseType.Trial
                ? $"体验模式 (剩余 {TrialUsesRemaining} 次)"
                : Type == LicenseType.Perpetual
                    ? "永久授权 (买断制)"
                    : $"订阅授权 (到期: {ExpiryDate:yyyy-MM-dd})",
            LicenseStatus.Expired => "订阅已过期 — 请续费",
            LicenseStatus.TrialExhausted => "体验次数已用完 — 请购买授权",
            LicenseStatus.Invalid => "授权无效",
            _ => "未激活"
        };
    }
}
