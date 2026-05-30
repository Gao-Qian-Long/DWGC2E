using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Core.Models;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Implements license management with hardware-bound activation codes,
/// trial tracking, and encrypted local storage.
///
/// Activation code formats:
/// - Perpetual: DWGT-PERM-{Base64(machineIdHash + checksum)}
/// - Subscription: DWGT-SUBS-{Base64(machineIdHash + expiryTicks + checksum)}
///
/// Commercial tiers:
/// - Trial: 3 free translation+export uses, no activation code needed
/// - Perpetual (买断制): ¥699 one-time, permanent use of current major version
/// - Subscription (订阅制): ¥49/month or ¥399/year, continuous updates + support
/// </summary>
[SupportedOSPlatform("windows")]
public class LicenseService : ILicenseService
{
    private const string LicenseFileName = "license.dat";
    private const int DefaultTrialUses = 3;
    private const string SecretKey = "DWG-Translator-2026-Secret-Key-v1";

    private readonly string _licensePath;
    private LicenseInfo _license = new();

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
                var json = Decrypt(encrypted);
                _license = JsonSerializer.Deserialize<LicenseInfo>(json) ?? new LicenseInfo();
            }
            else
            {
                // Initialize trial
                _license = new LicenseInfo
                {
                    Type = LicenseType.Trial,
                    TrialUsesRemaining = DefaultTrialUses,
                    MachineId = GetMachineId(),
                    FirstUseDate = DateTime.UtcNow
                };
                SaveLicense();
            }

            // Validate machine binding — use fuzzy match to survive minor hardware changes
            if (!string.IsNullOrEmpty(_license.MachineId) && !IsMachineIdMatch(_license.MachineId))
            {
                Log.Warning("License machine ID mismatch. Resetting to trial.");
                _license = new LicenseInfo
                {
                    Type = LicenseType.Trial,
                    TrialUsesRemaining = DefaultTrialUses,
                    MachineId = GetMachineId(),
                    FirstUseDate = DateTime.UtcNow
                };
                SaveLicense();
            }

            // Track first use date for trial
            if (_license.FirstUseDate == null)
            {
                _license.FirstUseDate = DateTime.UtcNow;
                SaveLicense();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load license — resetting to fresh trial");
            // Delete corrupted license file so activation can work
            try { if (File.Exists(_licensePath)) File.Delete(_licensePath); } catch { }
            _license = new LicenseInfo
            {
                Type = LicenseType.Trial,
                TrialUsesRemaining = DefaultTrialUses,
                MachineId = GetMachineId(),
                FirstUseDate = DateTime.UtcNow
            };
            SaveLicense();
        }
    }

    public void SaveLicense()
    {
        try
        {
            var json = JsonSerializer.Serialize(_license);
            var encrypted = Encrypt(json);
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
        if (_license.Type != LicenseType.Trial)
            return true; // Non-trial licenses don't consume uses

        if (_license.TrialUsesRemaining <= 0)
            return false;

        _license.TrialUsesRemaining--;
        SaveLicense();
        Log.Information("Trial use consumed. Remaining: {Remaining}", _license.TrialUsesRemaining);
        return true;
    }

    /// <summary>
    /// Gets the number of days since first use (for trial period display).
    /// </summary>
    public int DaysSinceFirstUse
    {
        get
        {
            if (_license.FirstUseDate == null) return 0;
            return (int)(DateTime.UtcNow - _license.FirstUseDate.Value).TotalDays;
        }
    }

    /// <summary>
    /// Fuzzy machine ID matching: allows minor hardware changes (e.g., USB devices)
    /// by checking if the primary identifiers (OS install date + CPU cores) match.
    /// </summary>
    private bool IsMachineIdMatch(string storedMachineId)
    {
        if (string.IsNullOrEmpty(storedMachineId)) return false;

        // Exact match
        var currentId = GetMachineId();
        if (currentId == storedMachineId) return true;

        // Fuzzy match: check if the stable components (OS install date portion) still match
        // This allows MachineName/UserName changes (e.g., domain join) but not OS reinstall
        try
        {
            var stableId = GetStableMachineId();
            // The stable portion is the last 8 chars derived from OS install date + CPU
            if (storedMachineId.Length >= 8 && stableId.Length >= 8)
            {
                return storedMachineId[^8..] == stableId[^8..];
            }
        }
        catch { /* fallback to exact match which already failed */ }

        return false;
    }

    // Special universal activation codes (not machine-bound, for VIP / internal use)
    private static readonly HashSet<string> SpecialPerpetualCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GQL119871"
    };

    public (bool Success, string Message) Activate(string activationCode)
    {
        if (string.IsNullOrWhiteSpace(activationCode))
            return (false, "激活码不能为空");

        var rawCode = activationCode.Trim();
        activationCode = rawCode.Replace("-", "").ToUpperInvariant();

        try
        {
            var machineId = GetMachineId();

            // Check special universal codes first (not machine-bound)
            if (SpecialPerpetualCodes.Contains(activationCode))
            {
                _license = new LicenseInfo
                {
                    Type = LicenseType.Perpetual,
                    MachineId = machineId,
                    ActivatedAt = DateTime.UtcNow,
                    ActivationCode = rawCode
                };
                SaveLicense();
                Log.Information("Special perpetual license activated with code {Code}", rawCode);
                return (true, "永久授权激活成功！(VIP 特殊授权)");
            }

            if (activationCode.StartsWith("DwgTranslator-P-"))
            {
                // Perpetual license
                var payload = activationCode["DwgTranslator-P-".Length..];
                var decoded = DecodePayload(payload);
                if (decoded == null || !ValidateChecksum(decoded))
                    return (false, "激活码无效或已损坏");

                var parts = decoded.Split('|');
                if (parts.Length < 2 || !IsMachineIdMatch(parts[0]))
                    return (false, "激活码与当前机器不匹配。如已更换硬件，请联系客服重新激活。");

                _license = new LicenseInfo
                {
                    Type = LicenseType.Perpetual,
                    MachineId = machineId,
                    ActivatedAt = DateTime.UtcNow,
                    ActivationCode = rawCode
                };
                SaveLicense();
                Log.Information("Perpetual license activated");
                return (true, "永久授权激活成功！感谢您购买 DWG Translator (买断制 ¥699)。");
            }
            else if (activationCode.StartsWith("DwgTranslator-S-"))
            {
                // Subscription license
                var payload = activationCode["DwgTranslator-S-".Length..];
                var decoded = DecodePayload(payload);
                if (decoded == null || !ValidateChecksum(decoded))
                    return (false, "激活码无效或已损坏");

                var parts = decoded.Split('|');
                if (parts.Length < 3 || !IsMachineIdMatch(parts[0]))
                    return (false, "激活码与当前机器不匹配。如已更换硬件，请联系客服重新激活。");

                if (!long.TryParse(parts[1], out var expiryTicks))
                    return (false, "激活码日期格式无效");

                var expiry = new DateTime(expiryTicks, DateTimeKind.Utc);
                if (expiry <= DateTime.UtcNow)
                    return (false, $"订阅授权已于 {expiry:yyyy-MM-dd} 过期，请续费。月费 ¥49 / 年费 ¥399");

                _license = new LicenseInfo
                {
                    Type = LicenseType.Subscription,
                    MachineId = machineId,
                    ExpiryDate = expiry,
                    ActivatedAt = DateTime.UtcNow,
                    ActivationCode = rawCode
                };
                SaveLicense();
                Log.Information("Subscription license activated until {Expiry}", expiry);
                return (true, $"订阅授权激活成功！有效期至 {expiry:yyyy-MM-dd}。");
            }

            return (false, "无法识别的激活码格式。\n正确格式: DwgTranslator-P-xxx (买断) 或 DwgTranslator-S-xxx (订阅)");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "License activation failed");
            return (false, $"激活失败: {ex.Message}");
        }
    }

    public string GenerateActivationRequest()
    {
        var machineId = GetMachineId();
        var hash = ComputeHash(machineId + "|" + DateTime.UtcNow.Ticks);
        return $"REQ-{machineId}-{hash[..8]}";
    }

    /// <summary>
    /// Generates a machine fingerprint based on stable hardware/OS identifiers.
    /// Uses multiple fallback strategies for resilience across reboots and minor hardware changes.
    /// </summary>
    public static string GetMachineId()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(Environment.MachineName);
            sb.Append("|");
            sb.Append(Environment.UserName);
            sb.Append("|");

            // OS install date from registry (stable across reboots)
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    var installDate = key.GetValue("InstallDate")?.ToString();
                    if (!string.IsNullOrEmpty(installDate))
                        sb.Append(installDate);
                }
            }
            catch { /* ignore registry errors */ }

            sb.Append("|");
            sb.Append(Environment.ProcessorCount);

            // Add disk volume serial for additional stability
            try
            {
                var drive = Path.GetPathRoot(Environment.SystemDirectory);
                if (!string.IsNullOrEmpty(drive))
                {
                    var volumeLabel = DriveInfo.GetDrives()
                        .FirstOrDefault(d => d.RootDirectory.FullName.StartsWith(drive))
                        ?.VolumeLabel ?? "";
                    sb.Append("|");
                    sb.Append(volumeLabel);
                }
            }
            catch { /* ignore */ }

            var hash = ComputeHash(sb.ToString());
            return hash[..16]; // 16-char hex fingerprint
        }
        catch
        {
            return ComputeHash(Environment.MachineName + Environment.UserName)[..16];
        }
    }

    /// <summary>
    /// Generates a stable machine ID based on OS-install-time + CPU only.
    /// Used for fuzzy matching when MachineName/UserName changes.
    /// </summary>
    private static string GetStableMachineId()
    {
        var sb = new StringBuilder();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var installDate = key.GetValue("InstallDate")?.ToString();
                if (!string.IsNullOrEmpty(installDate))
                    sb.Append(installDate);
            }
        }
        catch { /* ignore */ }

        sb.Append("|");
        sb.Append(Environment.ProcessorCount);

        return ComputeHash(sb.ToString())[..16];
    }

    #region Crypto Helpers

    private static byte[] Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        var key = DeriveKey(SecretKey, aes.KeySize / 8);
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return result;
    }

    private static string Decrypt(byte[] cipherData)
    {
        using var aes = Aes.Create();
        var key = DeriveKey(SecretKey, aes.KeySize / 8);
        aes.Key = key;

        // Extract IV
        var iv = new byte[aes.IV.Length];
        Buffer.BlockCopy(cipherData, 0, iv, 0, iv.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var cipherBytes = new byte[cipherData.Length - iv.Length];
        Buffer.BlockCopy(cipherData, iv.Length, cipherBytes, 0, cipherBytes.Length);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKey(string password, int keyBytes)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        var result = new byte[keyBytes];
        Buffer.BlockCopy(hash, 0, result, 0, keyBytes);
        return result;
    }

    private static string ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private static string? DecodePayload(string payload)
    {
        try
        {
            var bytes = Convert.FromBase64String(payload);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static bool ValidateChecksum(string payload)
    {
        var parts = payload.Split('|');
        if (parts.Length < 2) return false;

        var data = string.Join("|", parts[..^1]);
        var expectedHash = ComputeHash(data + SecretKey)[..8];
        return parts[^1] == expectedHash;
    }

    #endregion
}
