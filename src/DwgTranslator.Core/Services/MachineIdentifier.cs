using System.Text;

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

            var hash = LicenseCrypto.ComputeHash(sb.ToString());
            return hash[..16];
        }
        catch
        {
            return LicenseCrypto.ComputeHash(Environment.MachineName + Environment.UserName)[..16];
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

        return LicenseCrypto.ComputeHash(sb.ToString())[..16];
    }

    /// <summary>
    /// Fuzzy machine ID matching: allows minor hardware changes (e.g., USB devices)
    /// by checking if the primary identifiers (OS install date + CPU cores) match.
    /// </summary>
    public static bool IsMachineIdMatch(string storedMachineId)
    {
        if (string.IsNullOrEmpty(storedMachineId)) return false;

        var currentId = GetMachineId();
        if (currentId == storedMachineId) return true;

        try
        {
            var stableId = GetStableMachineId();
            if (storedMachineId.Length >= 8 && stableId.Length >= 8)
            {
                return storedMachineId[^8..] == stableId[^8..];
            }
        }
        catch { }

        return false;
    }
}
