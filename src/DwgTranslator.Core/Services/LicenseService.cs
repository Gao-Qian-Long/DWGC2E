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
/// </summary>
[SupportedOSPlatform("windows")]
public class LicenseService : ILicenseService
{
    private const string LicenseFileName = "license.dat";
    private const int DefaultTrialUses = 5;
    private const string SecretKey = "DWG-Translator-2026-Secret-Key-v1"; // Simple obfuscation key

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
                    MachineId = GetMachineId()
                };
                SaveLicense();
            }

            // Validate machine binding
            if (!string.IsNullOrEmpty(_license.MachineId) && _license.MachineId != GetMachineId())
            {
                Log.Warning("License machine ID mismatch. Resetting to trial.");
                _license = new LicenseInfo
                {
                    Type = LicenseType.Trial,
                    TrialUsesRemaining = DefaultTrialUses,
                    MachineId = GetMachineId()
                };
                SaveLicense();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load license");
            _license = new LicenseInfo
            {
                Type = LicenseType.Trial,
                TrialUsesRemaining = DefaultTrialUses,
                MachineId = GetMachineId()
            };
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

    // Special universal activation codes (not machine-bound, for VIP / internal use)
    private static readonly HashSet<string> SpecialPerpetualCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "POCKETTER"
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
                if (parts.Length < 2 || parts[0] != machineId)
                    return (false, "激活码与当前机器不匹配");

                _license = new LicenseInfo
                {
                    Type = LicenseType.Perpetual,
                    MachineId = machineId,
                    ActivatedAt = DateTime.UtcNow,
                    ActivationCode = rawCode
                };
                SaveLicense();
                Log.Information("Perpetual license activated");
                return (true, "永久授权激活成功！感谢您购买 DWG Translator。");
            }
            else if (activationCode.StartsWith("DwgTranslator-S-"))
            {
                // Subscription license
                var payload = activationCode["DwgTranslator-S-".Length..];
                var decoded = DecodePayload(payload);
                if (decoded == null || !ValidateChecksum(decoded))
                    return (false, "激活码无效或已损坏");

                var parts = decoded.Split('|');
                if (parts.Length < 3 || parts[0] != machineId)
                    return (false, "激活码与当前机器不匹配");

                if (!long.TryParse(parts[1], out var expiryTicks))
                    return (false, "激活码日期格式无效");

                var expiry = new DateTime(expiryTicks, DateTimeKind.Utc);
                if (expiry <= DateTime.UtcNow)
                    return (false, "订阅授权已过期，请续费");

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

            return (false, "无法识别的激活码格式");
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
    /// </summary>
    public static string GetMachineId()
    {
        try
        {
            // Combine multiple stable identifiers for resilience
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

            var hash = ComputeHash(sb.ToString());
            return hash[..16]; // 16-char hex fingerprint
        }
        catch
        {
            // Fallback to a less stable but functional ID
            return ComputeHash(Environment.MachineName + Environment.UserName)[..16];
        }
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
