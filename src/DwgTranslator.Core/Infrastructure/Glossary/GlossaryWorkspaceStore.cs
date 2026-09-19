using System.Text.Json;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;
namespace DwgTranslator.Core.Infrastructure.Glossary;

public sealed class GlossaryWorkspaceDocument
{
    public int Version { get; set; } = 2;
    public List<GlossaryEntry> Entries { get; set; } = new();
    public List<string> Categories { get; set; } = new() { EffectiveGlossary.DefaultCategory };
    public Dictionary<string, CloudGlossaryEntry> SyncBasis { get; set; } = new();
    public Dictionary<string, CloudGlossaryEntry> PendingUploads { get; set; } = new();
    public DateTime? LastCheckedAt { get; set; }
}
public static class GlossaryWorkspaceStore
{
    public static GlossaryWorkspaceDocument Load(string path)
    {
        var doc = JsonSerializer.Deserialize<GlossaryWorkspaceDocument>(File.ReadAllText(path), AppConfigJson.ReadOptions) ?? throw new InvalidDataException("术语库为空");
        if (doc.Version != 2 || doc.Entries == null || doc.Categories == null || doc.SyncBasis == null || doc.PendingUploads == null) throw new InvalidDataException("不支持的术语库版本");
        Normalize(doc); return doc;
    }
    public static void Normalize(GlossaryWorkspaceDocument doc)
    {
        foreach (var e in doc.Entries) EffectiveGlossary.Normalize(e);
        doc.Categories = new[] { EffectiveGlossary.DefaultCategory }.Concat(doc.Categories).Concat(doc.Entries.Select(e => e.Category)).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (doc.Entries.Select(e => e.LocalId).Distinct().Count() != doc.Entries.Count) throw new InvalidDataException("本机词条标识重复");
    }
    public static void Save(string path, GlossaryWorkspaceDocument doc)
    {
        Normalize(doc); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, JsonSerializer.Serialize(doc, AppConfigJson.WriteOptions)); _ = JsonSerializer.Deserialize<GlossaryWorkspaceDocument>(File.ReadAllText(tmp), AppConfigJson.ReadOptions) ?? throw new InvalidDataException(); File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public static GlossaryWorkspaceDocument Migrate(string path, string localDirectory, string bundledDirectory)
    {
        if (File.Exists(path)) return Load(path);
        var doc = new GlossaryWorkspaceDocument(); var files = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(bundledDirectory)) foreach (var f in Directory.GetFiles(bundledDirectory,"*.json")) files[Path.GetFileName(f)] = f;
        if (Directory.Exists(localDirectory)) foreach (var f in Directory.GetFiles(localDirectory,"*.json")) if (!string.Equals(f,path,StringComparison.OrdinalIgnoreCase)) files[Path.GetFileName(f)] = f;
        foreach (var f in files.Values)
        {
            var entries = JsonSerializer.Deserialize<List<GlossaryEntry>>(File.ReadAllText(f), AppConfigJson.ReadOptions) ?? throw new InvalidDataException($"术语库无效：{Path.GetFileName(f)}");
            var pairs = (from s in TranslationLanguages.All from t in TranslationLanguages.All where s != t && TranslationLanguages.GlossaryFileName(s.Code,t.Code) == Path.GetFileName(f) select (s.Code,t.Code)).ToList();
            foreach (var e in entries)
            {
                e.LocalId = Guid.NewGuid().ToString("D");
                if (pairs.Count == 1 && (string.IsNullOrEmpty(e.Direction) || e.Direction.Equals(pairs[0].Item1 + "-" + pairs[0].Item2,StringComparison.OrdinalIgnoreCase))) { e.SourceLang = pairs[0].Item1; e.TargetLang = pairs[0].Item2; }
                else { e.SourceLang = ""; e.TargetLang = ""; }
                doc.Entries.Add(e);
            }
            var backup = Path.Combine(localDirectory,"migration-v2-backup",Path.GetFileName(f));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!); if (!File.Exists(backup)) File.Copy(f,backup);
        }
        Save(path,doc); return doc;
    }
}
