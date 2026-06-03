using System.Runtime.Versioning;
using System.Text.Json;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Implements license management with hardware-bound activation codes,
/// trial tracking, and encrypted local storage.
/// </summary>
[SupportedOSPlatform("windows")]
public class LicenseService : ILicenseService
{
    private const string LicenseFileName = "license.dat";
    private const int DefaultTrialUses = 3;

    private readonly string _licensePath;
    private LicenseInfo _license = new();
    private readonly object _licenseLock = new();

    public LicenseService(string appDataDir)
    {
        _licensePath = Path.Combine(appDataDir, LicenseFileName);
    }

    public LicenseInfo CurrentLicense => _license;

    public void LoadLicense()
    {
        try
        {
            if (File.Exists(_licensePath))
            {
                var encrypted = File.ReadAllBytes(_licensePath);
                var json = LicenseCrypto.Decrypt(encrypted);
                _license = JsonSerializer.Deserialize<LicenseInfo>(json) ?? new LicenseInfo();
            }
            else
            {
                _license = new LicenseInfo
                {
                    Type = LicenseType.Trial,
                    TrialUsesRemaining = DefaultTrialUses,
                    MachineId = MachineIdentifier.GetMachineId(),
                    FirstUseDate = DateTime.UtcNow
                };
                SaveLicense();
            }

            if (!string.IsNullOrEmpty(_license.MachineId) && !MachineIdentifier.IsMachineIdMatch(_license.MachineId))
            {
                Log.Warning("License machine ID mismatch. Resetting to trial.");
                _license = new LicenseInfo
                {
                    Type = LicenseType.Trial,
                    TrialUsesRemaining = DefaultTrialUses,
                    MachineId = MachineIdentifier.GetMachineId(),
                    FirstUseDate = DateTime.UtcNow
                };
                SaveLicense();
            }

            if (_license.FirstUseDate == null)
            {
                _license.FirstUseDate = DateTime.UtcNow;
                SaveLicense();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load license");
            if (_license == null)
            {
                _license = new LicenseInfo
                {
                    Type = LicenseType.Trial,
                    TrialUsesRemaining = 0,
                    MachineId = MachineIdentifier.GetMachineId(),
                    FirstUseDate = DateTime.UtcNow
                };
            }
        }
    }

    public void SaveLicense()
    {
        try
        {
            var json = JsonSerializer.Serialize(_license);
            var encrypted = LicenseCrypto.Encrypt(json);
            Directory.CreateDirectory(Path.GetDirectoryName(_licensePath)!);
            File.WriteAllBytes(_licensePath, encrypted);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save license");
        }
    }

    public bool CanExecuteOperation()
    {
        return _license.IsValid;
    }

    public bool ConsumeTrialUse()
    {
        lock (_licenseLock)
        {
            if (_license.Type != LicenseType.Trial)
                return true;

            if (_license.TrialUsesRemaining <= 0)
                return false;

            _license.TrialUsesRemaining--;
        }
        SaveLicense();
        Log.Information("Trial use consumed. Remaining: {Remaining}", _license.TrialUsesRemaining);
        return true;
    }

    public int DaysSinceFirstUse
    {
        get
        {
            if (_license.FirstUseDate == null) return 0;
            return (int)(DateTime.UtcNow - _license.FirstUseDate.Value).TotalDays;
        }
    }

    public (bool Success, string Message) Activate(string activationCode)
    {
        if (string.IsNullOrWhiteSpace(activationCode))
            return (false, Strings.Get("LicenseActivationEmpty"));

        var rawCode = activationCode.Trim();
        activationCode = rawCode.Replace("-", "").ToUpperInvariant();

        try
        {
            var machineId = MachineIdentifier.GetMachineId();

            if (activationCode.StartsWith("DwgTranslator-P-"))
            {
                var payload = activationCode["DwgTranslator-P-".Length..];
                var decoded = LicenseCrypto.DecodePayload(payload);
                if (decoded == null || !LicenseCrypto.ValidateChecksum(decoded))
                    return (false, Strings.Get("LicenseActivationInvalid"));

                var parts = decoded.Split('|');
                if (parts.Length < 2 || !MachineIdentifier.IsMachineIdMatch(parts[0]))
                    return (false, Strings.Get("LicenseActivationMachineMismatch"));

                _license = new LicenseInfo
                {
                    Type = LicenseType.Perpetual,
                    MachineId = machineId,
                    ActivatedAt = DateTime.UtcNow,
                    ActivationCode = "HASH:" + LicenseCrypto.ComputeHash(rawCode)[..16]
                };
                SaveLicense();
                Log.Information("Perpetual license activated");
                return (true, Strings.Get("LicenseActivationSuccessPerpetual"));
            }
            else if (activationCode.StartsWith("DwgTranslator-S-"))
            {
                var payload = activationCode["DwgTranslator-S-".Length..];
                var decoded = LicenseCrypto.DecodePayload(payload);
                if (decoded == null || !LicenseCrypto.ValidateChecksum(decoded))
                    return (false, Strings.Get("LicenseActivationInvalid"));

                var parts = decoded.Split('|');
                if (parts.Length < 3 || !MachineIdentifier.IsMachineIdMatch(parts[0]))
                    return (false, Strings.Get("LicenseActivationMachineMismatch"));

                if (!long.TryParse(parts[1], out var expiryTicks))
                    return (false, Strings.Get("LicenseActivationDateInvalid"));

                var expiry = new DateTime(expiryTicks, DateTimeKind.Utc);
                if (expiry <= DateTime.UtcNow)
                    return (false, Strings.Get("LicenseActivationExpired", expiry.ToString("yyyy-MM-dd")));

                _license = new LicenseInfo
                {
                    Type = LicenseType.Subscription,
                    MachineId = machineId,
                    ExpiryDate = expiry,
                    ActivatedAt = DateTime.UtcNow,
                    ActivationCode = "HASH:" + LicenseCrypto.ComputeHash(rawCode)[..16]
                };
                SaveLicense();
                Log.Information("Subscription license activated until {Expiry}", expiry);
                return (true, Strings.Get("LicenseActivationSuccessSubscription", expiry.ToString("yyyy-MM-dd")));
            }

            return (false, Strings.Get("LicenseActivationInvalidFormat"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "License activation failed");
            return (false, Strings.Get("LicenseActivationFailed"));
        }
    }

    public string GenerateActivationRequest()
    {
        var machineId = MachineIdentifier.GetMachineId();
        var hash = LicenseCrypto.ComputeHash(machineId + "|" + DateTime.UtcNow.Ticks);
        return $"REQ-{machineId}-{hash[..8]}";
    }

    /// <summary>
    /// Gets the machine ID for external use (e.g., license generation).
    /// </summary>
    public static string GetMachineId() => MachineIdentifier.GetMachineId();
}
