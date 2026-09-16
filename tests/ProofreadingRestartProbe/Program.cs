using System.Security.Cryptography;
using System.Text.Json;
using ACadSharp;
using ACadSharp.IO;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

// Two separately launched processes: persist, exit, then restore with fresh reader/geometry.
if (args.Length != 2 || args[0] is not ("save" or "restore"))
    throw new ArgumentException("Usage: ProofreadingRestartProbe save|restore <isolated-evidence-directory>");
var root = Path.GetFullPath(args[1]);
var marker = Path.Combine(root, "proofreading-probe.json");
var source = Path.Combine(root, "source.dwg");
var store = new ProofreadingStore(Path.Combine(AccountWorkspace.DirectoryFor(root, "probe-owner"), "proofreading.json"));
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
if (args[0] == "save")
{
    if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        throw new IOException("Probe refuses a nonempty directory.");
    Directory.CreateDirectory(root);
    var doc = new CadDocument();
    doc.Entities.Add(new ACadSharp.Entities.MText { Value = "阀门反馈", Height = 3.5, RectangleWidth = 30 });
    DwgWriter.Write(source, doc);
    var entities = new DwgReaderService().ExtractFromFile(source);
    entities.Single().TranslatedText = "Manually reviewed valve feedback";
    store.Save(entities, entities);
    File.WriteAllText(marker, JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, SourceHash = Hash(source), RecordHash = Hash(store.FilePath) }));
    Console.WriteLine("PASS saved proofreading; process exits with no shared in-memory state");
}
else
{
    using var markerJson = JsonDocument.Parse(File.ReadAllText(marker));
    if (markerJson.RootElement.GetProperty("ProcessId").GetInt32() == Environment.ProcessId)
        throw new Exception("Must be a separate process.");
    var restored = store.Restore(p => new DwgReaderService().ExtractFromFile(p));
    var entity = restored.Entities.Single();
    if (entity.TranslatedText != "Manually reviewed valve feedback" || entity.Status != TranslationStatus.Reviewed
        || entity.MTextRectangleWidth != 30 || entity.Height != 3.5 || restored.SkippedSources.Count != 0)
        throw new Exception("Restored proofreading or fresh geometry differs.");
    if (Hash(source) != markerJson.RootElement.GetProperty("SourceHash").GetString()
        || Hash(store.FilePath) != markerJson.RootElement.GetProperty("RecordHash").GetString())
        throw new Exception("Restoration modified source drawing or saved record.");
    var other = new ProofreadingStore(Path.Combine(AccountWorkspace.DirectoryFor(root, "other-owner"), "proofreading.json"));
    if (other.Restore(_ => throw new Exception("Wrong account attempted extraction")).Entities.Count != 0)
        throw new Exception("Cross-account leak.");
    File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new {
        Passed = true, SeparateProcess = true, SourceUnchanged = true, RecordUnchanged = true,
        AccountIsolation = true, FreshGeometry = true, ProcessId = Environment.ProcessId,
        Scope = "Core persistence and real DWG reader; not GUI process or production cloud"
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS separate-process recovery, account isolation, fresh geometry and unchanged source/record");
}
