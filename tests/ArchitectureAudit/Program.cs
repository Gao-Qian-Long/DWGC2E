// Read-only compiled dependency/resource audit. Does not load or execute application/CAD assemblies.
// Usage: dotnet run --project tests/ArchitectureAudit -c Release -- <workspace> <new-report.json> [payload-directory]
using System.Collections;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length is < 2 or > 3) throw new ArgumentException("Expected workspace, new report path, and optional payload directory.");
var root = Path.GetFullPath(args[0]);
var report = Path.GetFullPath(args[1]);
var payloadRoot = args.Length == 3 ? Path.GetFullPath(args[2]) : Path.Combine(root, "release");
if (!report.StartsWith(Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(report))
    throw new ArgumentException("Evidence must be a new file under workspace artifacts.");
var checks = new List<object>();
var failures = new List<string>();
void Check(bool pass, string name, object? details = null)
{
    checks.Add(new { name, pass, details });
    if (!pass) failures.Add(name);
    Console.WriteLine((pass ? "PASS " : "FAIL ") + name);
}
string At(string relative) => relative.StartsWith("release/", StringComparison.Ordinal)
    ? Path.Combine(payloadRoot, relative[8..].Replace('/', Path.DirectorySeparatorChar))
    : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
var forbiddenAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "DwgTranslator", "DwgTranslator.App", "PresentationCore", "PresentationFramework", "WindowsBase", "System.Windows.Forms" };
var assemblyEvidence = new List<object>();
foreach (var relative in new[] {
    "src/DwgTranslator.Core/bin/Release/net8.0/DwgTranslator.Core.dll",
    "release/CadPlugin/DwgTranslator.Core.dll",
    "release/CadPlugin/DwgTranslator.Cad.dll" })
{
    using var input = File.OpenRead(At(relative));
    using var pe = new PEReader(input);
    var md = pe.GetMetadataReader();
    var references = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).Order().ToArray();
    var forbidden = references.Where(forbiddenAssemblies.Contains).ToArray();
    Check(forbidden.Length == 0, relative + " has no direct UI assembly dependency", forbidden);
    var uiTypes = md.TypeReferences.Select(h => md.GetTypeReference(h)).Where(t =>
        md.GetString(t.Namespace) is "System.Windows" or "System.Windows.Controls" or "System.Windows.Forms")
        .Select(t => md.GetString(t.Namespace) + "." + md.GetString(t.Name)).ToArray();
    Check(uiTypes.Length == 0, relative + " has no WPF/WinForms UI type references", uiTypes);
    assemblyEvidence.Add(new { path = relative, sha256 = Hash(File.ReadAllBytes(At(relative))), references });
}
var appPath = At("src/DwgTranslator.App/bin/Release/net8.0-windows/win-x64/DwgTranslator.dll");
using var appStream = File.OpenRead(appPath);
using var appPe = new PEReader(appStream);
var appMd = appPe.GetMetadataReader();
var resourceData = appPe.GetSectionData(appPe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
var embedded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
foreach (var handle in appMd.ManifestResources)
{
    var item = appMd.GetManifestResource(handle);
    if (!item.Implementation.IsNil) continue;
    var blob = resourceData.GetReader(checked((int)item.Offset), resourceData.Length - checked((int)item.Offset));
    var length = blob.ReadInt32();
    embedded.Add(appMd.GetString(item.Name), blob.ReadBytes(length));
}
const string prefix = "DwgTranslator.App.Embedded.CadPlugin.";
var payload = embedded.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
Check(payload.Length > 0, "APP contains embedded CAD fallback");
var payloadEvidence = new List<object>();
foreach (var resource in payload)
{
    var name = resource.Key[prefix.Length..].Replace('\\', '/');
    var target = Path.GetFullPath(At("release/CadPlugin/" + name));
    var safe = target.StartsWith(At("release/CadPlugin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    var same = safe && File.Exists(target) && Hash(resource.Value) == Hash(File.ReadAllBytes(target));
    Check(same, "Embedded CAD payload equals payload file: " + name);
    payloadEvidence.Add(new { name, sha256 = Hash(resource.Value), same });
}
var declared = File.ReadAllLines(At("release/CadPlugin/cad-files.txt")).Append("cad-files.txt").Order().ToArray();
var actual = payload.Select(x => x.Key[prefix.Length..].Replace('\\', '/')).Order().ToArray();
Check(declared.Select(x => x.Replace('\\', '/')).SequenceEqual(actual), "Embedded fallback and selected payload manifest cover exactly the same files");
var wpfResource = embedded.Single(x => x.Key.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));
using var wpfStream = new MemoryStream(wpfResource.Value);
using var reader = new ResourceReader(wpfStream);
var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var enumerator = reader.GetEnumerator();
while (enumerator.MoveNext()) keys.Add((string)enumerator.Key);
Check(keys.Contains("icon.ico"), "WPF application icon remains embedded");
var appRoot = At("src/DwgTranslator.App");
foreach (var file in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
    .Where(f => !Path.GetRelativePath(appRoot, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")))
{
    var key = Path.ChangeExtension(Path.GetRelativePath(appRoot, file), ".baml").Replace('\\', '/');
    Check(keys.Contains(key), "Compiled WPF resource exists: " + key);
}
Check(Hash(File.ReadAllBytes(At("assets/glossaries/mechanical_zh_en.json"))) == Hash(File.ReadAllBytes(At("release/assets/default-glossaries/mechanical_zh_en.json"))), "Payload immutable default glossary matches source");
var info = JsonDocument.Parse(File.ReadAllText(At("release/build-info.json")));
var installedHash = Hash(File.ReadAllBytes(At("release/DwgTranslator.exe")));
Check(installedHash == info.RootElement.GetProperty("sha256").GetString(), "Payload executable hash matches delivery record");
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
File.WriteAllText(report, JsonSerializer.Serialize(new {
    createdAt = DateTimeOffset.Now, passed = failures.Count == 0, payloadRoot,
    scope = "Metadata-only direct references and compiled resources. Does not execute CAD, prove transitive package purity, dynamic reflection reachability, GUI behavior, or identity of the separately inspected build DLL inside the compressed single-file executable.",
    releaseVersion = info.RootElement.GetProperty("version").GetString(), installedHash,
    appAssemblySha256 = Hash(File.ReadAllBytes(appPath)), assemblies = assemblyEvidence,
    embeddedPayload = payloadEvidence, checks, failures
}, new JsonSerializerOptions { WriteIndented = true }));
return failures.Count == 0 ? 0 : 1;
