using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using DwgTranslator.App.ViewModels;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private static void VerifyPluginSourceConsistency(MainViewModel vm)
    {
        var field = typeof(MainViewModel).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var config = (DwgTranslator.Core.Models.AppConfig)field.GetValue(vm)!;
        var previous = config.CadPluginPath;
        var custom = Path.Combine(AppDataDir, "stale-plugin-fixture", "DwgTranslator.Cad.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.WriteAllText(custom, "inert old plugin path fixture");
        try
        {
            config.CadPluginPath = custom;
            var bundled = Path.Combine(AppContext.BaseDirectory, "CadPlugin", "DwgTranslator.Cad.dll");
            var flat = Path.Combine(AppContext.BaseDirectory, "DwgTranslator.Cad.dll");
            var expected = File.Exists(bundled) ? bundled : File.Exists(flat) ? flat : custom;
            Check(vm.CadPluginDirectory == Path.GetDirectoryName(expected), "environment follows bundled-flat-custom priority");
            var resolve = typeof(DwgTranslator.App.Services.AutoCadInteropService).GetMethod("ResolveCadPluginPath", BindingFlags.NonPublic | BindingFlags.Static)!;
            var actual = (string?)resolve.Invoke(null, new object[] { config });
            Check(actual == expected, "translation and environment select same plugin source");
            Check(config.CadPluginPath == expected, "translation refreshes selected plugin config");
            Check(File.ReadAllText(custom) == "inert old plugin path fixture", "old plugin is not deleted or overwritten");
        }
        finally { config.CadPluginPath = previous; }
    }

    private static void VerifyEmbeddedCadPlugin()
    {
        var assembly = typeof(MainViewModel).Assembly;
        const string prefix = "DwgTranslator.App.Embedded.CadPlugin.";
        var resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DwgTranslator.sln"))) root = root.Parent;
        if (root == null) throw new DirectoryNotFoundException("UI smoke must run beneath the source workspace.");
        using var platformStream = assembly.GetManifestResourceStream(prefix + "cad-platform.txt")!;
        using var platformReader = new StreamReader(platformStream);
        var framework = platformReader.ReadToEnd().Trim() == "GstarCAD" ? "net48" : "net8.0";
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var bundled = Path.Combine(root.FullName, "src", "DwgTranslator.Cad", "bin", configuration, framework);
        var extracted = Path.Combine(AppDataDir, "embedded-plugin-test");
        var extract = typeof(MainViewModel).GetMethod("ExtractEmbeddedCadPlugin", BindingFlags.NonPublic | BindingFlags.Static)!;
        void Extract() => extract.Invoke(null, new object[] { extracted });
        foreach (var name in new[] { "DwgTranslator.Cad.dll", "DwgTranslator.Core.dll", "cad-platform.txt" })
            Check(resources.Contains(prefix + name), "embedded plugin includes " + name);
        Extract();
        foreach (var resource in resources)
        {
            var relative = resource[prefix.Length..];
            using var source = assembly.GetManifestResourceStream(resource)!;
            var expected = SHA256.HashData(source);
            Check(expected.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(bundled, relative)))), "embedded matches current build: " + relative);
            Check(expected.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(extracted, relative)))), "fallback extracts: " + relative);
        }
        var dll = Path.Combine(extracted, "DwgTranslator.Cad.dll");
        var original = File.ReadAllBytes(dll);
        var corrupted = (byte[])original.Clone(); corrupted[0] ^= 0xff;
        File.WriteAllBytes(dll, corrupted);
        Extract();
        Check(File.ReadAllBytes(dll).SequenceEqual(original), "fallback repairs equal-length corruption");
        var unchangedTime = File.GetLastWriteTimeUtc(dll);
        Extract();
        Check(File.GetLastWriteTimeUtc(dll) == unchangedTime, "identical fallback file is not rewritten");
        File.WriteAllBytes(dll, corrupted);
        using (var locked = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var refused = false;
            try { Extract(); } catch (TargetInvocationException ex) when (ex.InnerException is IOException) { refused = true; }
            Check(refused, "locked fallback replacement fails safely");
            Check(File.ReadAllBytes(dll).SequenceEqual(corrupted), "locked fallback file is not truncated");
        }
        Check(!Directory.EnumerateFiles(extracted, "*.tmp", SearchOption.AllDirectories).Any(), "failed fallback replacement leaves no temporary files");
        Extract();
        Check(File.ReadAllBytes(dll).SequenceEqual(original), "fallback replacement recovers after unlocking");
    }
}
