using DwgTranslator.Core.Resources;

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
                ? Strings.Get("LicenseStatusTrial", TrialUsesRemaining)
                : Type == LicenseType.Perpetual
                    ? Strings.Get("LicenseStatusPerpetual")
                    : Strings.Get("LicenseStatusSubscription", $"{ExpiryDate:yyyy-MM-dd}"),
            LicenseStatus.Expired => Strings.Get("LicenseStatusExpired"),
            LicenseStatus.TrialExhausted => Strings.Get("LicenseStatusTrialExhausted"),
            LicenseStatus.Invalid => Strings.Get("LicenseStatusInvalid"),
            _ => Strings.Get("LicenseNotActivated")
        };
    }
}
