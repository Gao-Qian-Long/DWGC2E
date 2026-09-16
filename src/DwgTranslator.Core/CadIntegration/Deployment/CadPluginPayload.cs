using System.Security.Cryptography;
using System.Text.Json;

namespace DwgTranslator.Core.Services;

/// <summary>Explicit private payload; never infer ownership from every file in the CAD support folder.</summary>
internal static class CadPluginPayload
{
    internal const string ManifestName = "cad-files.txt";
    internal const string ReceiptName = "dwgc2e-installed-files.json";
    internal static readonly string[] LegacyFiles = [CadPluginInstaller.PluginFileName, CadPluginInstaller.CoreFileName, CadPluginInstaller.PlatformFileName];

    internal static string Resolve(string directory, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(' ') || p.EndsWith('.') ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new IOException("插件文件清单包含不安全路径。");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("插件路径越界。");
        // Reject redirected ancestors as well as leaf links before copying or deleting files.
        for (var current = full; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("插件路径不能包含目录联接或符号链接。");
        return full;
    }

    internal static string[] Read(string directory)
    {
        var manifest = Resolve(directory, ManifestName);
        if (!File.Exists(manifest)) return LegacyFiles;
        var names = File.ReadAllLines(manifest);
        if (names.Length == 0 || names.Length > 2048) throw new IOException("插件文件清单为空或过大。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            Resolve(directory, name);
            var leaf = name.Replace('\\', '/').Split('/')[^1];
            if (new[] { "AcCoreMgd.dll", "AcDbMgd.dll", "AcMgd.dll", "AcCui.dll", "GcCoreMgd.dll", "GcDbMgd.dll", "GcMgd.dll" }
                .Contains(leaf, StringComparer.OrdinalIgnoreCase))
                throw new IOException("插件清单不能包含CAD宿主SDK程序集。");
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(ReceiptName, StringComparison.OrdinalIgnoreCase) || !seen.Add(name.Replace('\\', '/')))
                throw new IOException("插件文件清单包含重复或保留文件名。");
        }
        if (LegacyFiles.Any(n => !seen.Contains(n))) throw new IOException("插件文件清单缺少必要文件。");
        return [.. names, ManifestName];
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static Dictionary<string, string> ReadReceipt(string directory)
    {
        var path = Resolve(directory, ReceiptName);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new IOException("插件安装记录无效。");
        if (entries.Count > 2049) throw new IOException("插件安装记录过大。");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in entries)
        {
            Resolve(directory, name);
            if (name.Equals(ReceiptName, StringComparison.OrdinalIgnoreCase) || hash is null || hash.Length != 64 ||
                !hash.All(Uri.IsHexDigit) || !result.TryAdd(name.Replace('\\', '/'), hash))
                throw new IOException("插件安装记录无效。");
        }
        return result;
    }

    internal static void WriteReceipt(string directory, IEnumerable<string> names)
    {
        // Retain earlier owned files for hash-protected uninstall, including dependencies dropped by an upgrade.
        var entries = ReadReceipt(directory);
        foreach (var name in names) entries[name.Replace('\\', '/')] = Hash(Resolve(directory, name));
        var destination = Resolve(directory, ReceiptName);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            SafeFileCommit.Commit(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
