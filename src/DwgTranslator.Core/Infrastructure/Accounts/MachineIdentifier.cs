using System.Text;
using System.Security.Cryptography;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Generates machine fingerprints based on stable hardware/OS identifiers.
/// Used for license machine binding with fuzzy matching support.
/// </summary>
internal static class MachineIdentifier
{
    /// <summary>
    /// Generates a machine fingerprint based on stable hardware/OS identifiers.
    /// Uses multiple fallback strategies for resilience across reboots and minor hardware changes.
    /// </summary>
    private static string ComputeHash(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    public static string GetMachineId()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(Environment.MachineName);
            sb.Append("|");
            sb.Append(Environment.UserName);
            sb.Append("|");

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
            catch { }

            sb.Append("|");
            sb.Append(Environment.ProcessorCount);

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
            catch { }

            var hash = ComputeHash(sb.ToString());
            return hash[..16];
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
    public static string GetStableMachineId()
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
        catch { }

        sb.Append("|");
        sb.Append(Environment.ProcessorCount);

        return ComputeHash(sb.ToString())[..16];
    }

    /// <summary>
    /// Prefer the stable machine ID for license binding so renames do not break licenses.
    /// Full fingerprint is retained for display/diagnostics via <see cref="GetMachineId"/>.
    /// </summary>
    public static string GetLicenseBindingId() => GetStableMachineId();

    /// <summary>
    /// Machine ID matching for license binding.
    /// Accepts either the full fingerprint or the stable fingerprint so that:
    /// - codes generated against either ID form validate
    /// - machine rename / username change do not invalidate an activated license
    /// </summary>
    public static bool IsMachineIdMatch(string storedMachineId)
    {
        if (string.IsNullOrEmpty(storedMachineId)) return false;

        var currentId = GetMachineId();
        if (string.Equals(currentId, storedMachineId, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var stableId = GetStableMachineId();
            if (string.Equals(stableId, storedMachineId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }

        return false;
    }
}
