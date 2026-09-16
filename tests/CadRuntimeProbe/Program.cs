using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Load the delivered reader and dependencies, not a newly compiled Core or a mock.
if (args.Length != 4 || args[0] is not ("prepare" or "verify" or "pipeline" or "worker-pipeline" or "worker-crash-before-write" or "worker-resume"))
    throw new ArgumentException("prepare|verify|pipeline|worker-pipeline RELEASE SOURCE_DWG EVIDENCE_DIRECTORY");
var mode = args[0];
var release = Path.GetFullPath(args[1]);
var source = Path.GetFullPath(args[2]);
var root = Path.GetFullPath(args[3]);
const string replacement = "Dust removal valve closed position feedback";
string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
var bundle = new InstalledBundle(Path.Combine(release, "DwgTranslator.exe"));
AssemblyLoadContext.Default.Resolving += (_, name) => {
    var bytes = bundle.ReadAssembly(name.Name + ".dll");
    return bytes is null ? null : AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(bytes));
};
var coreBytes = bundle.ReadAssembly("DwgTranslator.Core.dll") ?? throw new InvalidDataException("Core missing from installed bundle");
var coreHash = Convert.ToHexString(SHA256.HashData(coreBytes));
var core = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(coreBytes));
if (mode is "pipeline" or "worker-pipeline" or "worker-crash-before-write" or "worker-resume") { await PipelineAcceptance.Run(core, bundle, release, source, root, mode != "pipeline", mode); return; }
var readerType = core.GetType("DwgTranslator.Core.Services.DwgReaderService", true)!;
var reader = Activator.CreateInstance(readerType)!;
var extract = readerType.GetMethod("ExtractFromFile")!;
object Read(string p) => extract.Invoke(reader, new object[] { p })!;
object? Get(object e, string name) => e.GetType().GetProperty(name)!.GetValue(e);
string Text(object e, string name) => Get(e, name)?.ToString() ?? "";
void Write(string name, object data) => File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
var sourceEntities = Read(source);
var rows = ((IEnumerable)sourceEntities).Cast<object>().ToArray();
Require(rows.Length == 6, "Expected six CAD-generated source entities");
Require(rows.Count(e => Text(e, "EntityType") == "DBText") == 3 && rows.Count(e => Text(e, "EntityType") == "MText") == 3, "Unexpected entity types");
Require(rows.All(e => Text(e, "PlainText") == "阀门反馈"), "Source text differs from fixture");
Require(rows.Select(e => Text(e, "Handle")).Distinct().Count() == 6, "Duplicate source handles");
Require(rows.All(e => string.Equals(Text(e, "SourceFilePath"), source, StringComparison.OrdinalIgnoreCase)), "Wrong source-file attribution");
var output = Path.Combine(root, "reader-roundtrip.dwg");
if (mode == "prepare") {
    Require(!Directory.Exists(root), "Evidence directory must be new");
    Directory.CreateDirectory(root);
    Write("source-extraction.json", sourceEntities);
    Write("before.json", new { Source = source, SourceHash = Hash(source), CoreHash = coreHash, ExecutableHash = bundle.ExecutableHash, PluginHash = Hash(Path.Combine(release,"CadPlugin","DwgTranslator.Cad.dll")) });
    foreach (var e in rows) {
        e.GetType().GetProperty("TranslatedText")!.SetValue(e, replacement);
        var status = e.GetType().GetProperty("Status")!;
        status.SetValue(e, Enum.Parse(status.PropertyType, "Translated"));
    }
    Write("writeback.json", new { SourceDwgPath = source, OutputDwgPath = output, OverwriteExisting = false, TargetIsCjk = false, Entities = sourceEntities, SessionId = "installed-reader-roundtrip", DoneSignalPath = Path.Combine(root,"writeback.done") });
    Console.WriteLine("PREPARED: six entities extracted by installed APP Core; no network or translation provider invoked.");
} else {
    var before = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"before.json"))).RootElement;
    Require(Hash(source) == before.GetProperty("SourceHash").GetString(), "Source drawing changed");
    Require(bundle.ExecutableHash == before.GetProperty("ExecutableHash").GetString(), "Installed EXE changed during acceptance");
    Require(coreHash == before.GetProperty("CoreHash").GetString(), "Installed reader changed during acceptance");
    Require(Hash(Path.Combine(release,"CadPlugin","DwgTranslator.Cad.dll")) == before.GetProperty("PluginHash").GetString(), "Plugin changed during acceptance");
    var done = File.ReadAllText(Path.Combine(root,"writeback.done")).Split('|');
    Require(done[0] == "success" && done[1] == "installed-reader-roundtrip" && done[2] == "6" && done[3] == "0", "Writeback did not report 6/0 success for this session");
    var actual = Read(output);
    var outputRows = ((IEnumerable)actual).Cast<object>().ToArray();
    Write("output-extraction.json", actual);
    Require(outputRows.Length == 6, "Output entity count changed");
    foreach (var e in rows) {
        var match = outputRows.Single(o => Text(o,"Handle") == Text(e,"Handle"));
        Require(Text(match,"PlainText") == replacement, "Replacement content mismatch: " + Text(e,"Handle"));
        Require(Text(match,"EntityType") == Text(e,"EntityType"), "Entity type changed");
        Require(Math.Abs(Convert.ToDouble(Get(match,"Rotation")) - Convert.ToDouble(Get(e,"Rotation"))) < 1e-6, "Rotation changed");
        Require(string.Equals(Text(match,"SourceFilePath"), output, StringComparison.OrdinalIgnoreCase), "Output attribution is stale");
    }
    Require(!Directory.EnumerateFiles(root,"*.tmp.dwg").Any(), "Temporary drawings leaked");
    Write("verification.json", new { Passed = true, Entities = 6, SourcePreserved = true, HandlesPreserved = true, EntityTypesPreserved = true, RotationsPreserved = true, ContentExact = true, SourceAttributionCorrect = true, CoreHash = coreHash, OutputHash = Hash(output), Scope = "Installed APP reader -> real CAD writeback -> installed APP reader; deterministic replacement, not UI scheduling or cloud translation" });
    Console.WriteLine("PASS: installed APP reader -> real CAD -> installed APP reader, 6/6 entities; source, handles, types and rotations preserved.");
}
