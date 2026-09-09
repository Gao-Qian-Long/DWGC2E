using Microsoft.Win32;
using System.Runtime.Versioning;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// AutoCAD installation detector — searches Windows registry for installed AutoCAD versions.
/// </summary>
#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
public static class AutoCadDetector
{
    /// <summary>Result of an AutoCAD detection attempt.</summary>
    public class DetectionResult
    {
        public bool Found { get; set; }
        public string InstallPath { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Detect the latest AutoCAD installation by scanning the registry.
    /// Searches both 64-bit and 32-bit registry views.
    /// </summary>
    public static DetectionResult DetectInstallation()
    {
        foreach (var path in new[]
        {
            @"D:\haochenCAD\浩辰CAD 2024",
            @"C:\Program Files\Gstarsoft\GstarCAD 2024"
        })
        {
            if (IsValidAutoCadPath(path))
            {
                return new DetectionResult
                {
                    Found = true,
                    InstallPath = path,
                    Version = "2024",
                    ProductName = "GstarCAD/浩辰CAD"
                };
            }
        }

        // GstarCAD/浩辰CAD installations do not always register under Autodesk keys.
        // Prefer the standard Windows uninstall records when InstallLocation is present.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var gstarResult = ScanInstalledProducts(view);
            if (gstarResult.Found) return gstarResult;
        }

        // Priority-ordered registry paths to check
        var registryPaths = new[]
        {
            @"SOFTWARE\Autodesk\AutoCAD",
            @"SOFTWARE\WOW6432Node\Autodesk\AutoCAD"
        };

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        foreach (var regPath in registryPaths)
        {
            try
            {
                using var key = hklm.OpenSubKey(regPath);
                if (key == null) continue;

                var result = ScanAutoCadVersions(key);
                if (result.Found) return result;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to read registry path: {Path}", regPath);
            }
        }

        // Also try 32-bit registry view explicitly
        try
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key32 = hklm32.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
            if (key32 != null)
            {
                var result = ScanAutoCadVersions(key32);
                if (result.Found) return result;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read 32-bit registry");
        }

        return new DetectionResult { Found = false };
    }

    private static DetectionResult ScanInstalledProducts(RegistryView view)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) return new DetectionResult();

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var product = uninstall.OpenSubKey(subKeyName);
                var displayName = product?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName) ||
                    (!displayName.Contains("GstarCAD", StringComparison.OrdinalIgnoreCase) &&
                     !displayName.Contains("浩辰CAD", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var installLocation = product?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation) && IsValidAutoCadPath(installLocation))
                {
                    return new DetectionResult
                    {
                        Found = true,
                        InstallPath = installLocation.TrimEnd(Path.DirectorySeparatorChar),
                        Version = product?.GetValue("DisplayVersion") as string ?? string.Empty,
                        ProductName = displayName
                    };
                }

                var displayIcon = product?.GetValue("DisplayIcon") as string;
                if (!string.IsNullOrWhiteSpace(displayIcon))
                {
                    var executable = displayIcon.Trim('"').Split(',')[0];
                    var directory = Path.GetDirectoryName(executable);
                    if (!string.IsNullOrWhiteSpace(directory) && IsValidAutoCadPath(directory))
                    {
                        return new DetectionResult
                        {
                            Found = true,
                            InstallPath = directory,
                            Version = product?.GetValue("DisplayVersion") as string ?? string.Empty,
                            ProductName = displayName
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to scan installed CAD products ({View})", view);
        }

        return new DetectionResult();
    }

    /// <summary>
    /// Check if the specified path looks like a valid AutoCAD installation
    /// (contains acad.exe).
    /// </summary>
    public static bool IsValidAutoCadPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return File.Exists(Path.Combine(path, "acad.exe")) ||
                   File.Exists(Path.Combine(path, "gcad.exe"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Try to locate DwgTranslator.Cad.dll in common locations.
    /// </summary>
    public static string? FindCadPlugin(string? baseDirectory = null)
    {
        var appDir = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        var searchDirs = new List<string>();

        // 1. Bundled plugin directory, then the legacy same-directory layout.
        if (!string.IsNullOrEmpty(appDir))
        {
            searchDirs.Add(Path.Combine(appDir, "CadPlugin"));
            searchDirs.Add(appDir);
        }

        // 2. Publish output directory (single-file publish scenario)
        if (!string.IsNullOrEmpty(appDir))
        {
            var publishDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "publish"));
            searchDirs.Add(publishDir);
        }

        // 3. Sibling build output directories (dev mode)
        //    exe: src\DwgTranslator.App\bin\Debug\net8.0-windows\
        //    cad: src\DwgTranslator.Cad\bin\Debug\net8.0\
        if (!string.IsNullOrEmpty(appDir))
        {
            var srcDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));
            foreach (var framework in new[] { "net48", "net8.0" })
            {
                searchDirs.Add(Path.Combine(srcDir, "DwgTranslator.Cad", "bin", "Debug", framework));
                searchDirs.Add(Path.Combine(srcDir, "DwgTranslator.Cad", "bin", "Release", framework));
            }
        }

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var path = Path.Combine(dir, "DwgTranslator.Cad.dll");
            if (File.Exists(path))
            {
                Log.Information("Found DwgTranslator.Cad.dll at: {Path}", path);
                return path;
            }
        }

        return null;
    }

    private static DetectionResult ScanAutoCadVersions(RegistryKey autoCadKey)
    {
        // Each subkey is a release version (e.g., R24.0, R25.0, R26.0)
        // We want the latest one, so sort descending
        var versionSubKeys = autoCadKey.GetSubKeyNames()
            .Where(name => name.StartsWith("R", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var versionName in versionSubKeys)
        {
            try
            {
                using var versionKey = autoCadKey.OpenSubKey(versionName);
                if (versionKey == null) continue;

                // Under each version, there are locale-specific subkeys (e.g., ACAD-1001:409)
                foreach (var subKeyName in versionKey.GetSubKeyNames())
                {
                    if (!subKeyName.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        using var installKey = versionKey.OpenSubKey(subKeyName);
                        if (installKey == null) continue;

                        var location = installKey.GetValue("AcadLocation") as string;
                        if (string.IsNullOrEmpty(location)) continue;

                        if (IsValidAutoCadPath(location))
                        {
                            var productName = installKey.GetValue("ProductName") as string ?? "AutoCAD";
                            Log.Information("Detected AutoCAD: {Product} at {Path} (registry: {Version}/{SubKey})",
                                productName, location, versionName, subKeyName);

                            return new DetectionResult
                            {
                                Found = true,
                                InstallPath = location,
                                Version = versionName,
                                ProductName = productName
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Failed to read install subkey: {SubKey}", subKeyName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to read version subkey: {Version}", versionName);
            }
        }

        return new DetectionResult { Found = false };
    }
}
