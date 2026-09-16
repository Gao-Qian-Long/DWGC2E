using System.Security.Cryptography;
using System.Text.Json;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>Account-local, explicitly saved proofreading. Never supplies geometry to CAD writers.</summary>
public sealed class ProofreadingStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath { get; }
    public ProofreadingStore(string filePath) => FilePath = Path.GetFullPath(filePath);

    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<Drawing> Drawings { get; set; } = new();
    }
    public sealed class Drawing
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public List<Entry> Entries { get; set; } = new();
    }
    public sealed class Entry
    {
        public string Handle { get; set; } = "";
        public string PlainText { get; set; } = "";
        public string RawText { get; set; } = "";
        public string EntityType { get; set; } = "";
        public string TranslatedText { get; set; } = "";
        public TranslationStatus Status { get; set; }
        public bool GlossaryHit { get; set; }
        public string Notes { get; set; } = "";
    }
    public sealed class Restoration
    {
        public List<TextEntity> Entities { get; } = new();
        public List<string> Sources { get; } = new();
        public List<string> SkippedSources { get; } = new();
    }

    private Snapshot Read()
    {
        if (!File.Exists(FilePath)) return new();
        var json = File.ReadAllText(FilePath);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(nameof(Snapshot.Version), out _)
            || !document.RootElement.TryGetProperty(nameof(Snapshot.Drawings), out _))
            throw new InvalidDataException("校对记录结构不完整，原文件已保留。");
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json, Options)
            ?? throw new InvalidDataException("校对记录为空，原文件已保留。");
        if (snapshot.Version != 1 || snapshot.Drawings == null)
            throw new InvalidDataException("校对记录版本无法读取，原文件已保留。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drawing in snapshot.Drawings)
        {
            if (drawing == null || string.IsNullOrWhiteSpace(drawing.Path) || !Path.IsPathFullyQualified(drawing.Path)
                || !paths.Add(Path.GetFullPath(drawing.Path)) || drawing.Sha256 == null || drawing.Sha256.Length != 64
                || drawing.Entries == null)
                throw new InvalidDataException("校对图纸记录无效，原文件已保留。");
            var handles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in drawing.Entries)
                if (entry == null || string.IsNullOrWhiteSpace(entry.Handle) || !handles.Add(entry.Handle)
                    || entry.PlainText == null || entry.RawText == null || entry.EntityType == null
                    || entry.TranslatedText == null || entry.Notes == null || !Enum.IsDefined(entry.Status))
                    throw new InvalidDataException("校对条目无效或重复，原文件已保留。");
        }
        return snapshot;
    }

    public void Save(IReadOnlyCollection<TextEntity> entities, IReadOnlyCollection<TextEntity> edited)
    {
        lock (Gate)
        {
            _ = Read(); // Do not silently overwrite corrupt or newer-version records.
            var dirty = edited.ToHashSet();
            if (dirty.Any(e => !entities.Contains(e))) throw new InvalidDataException("校对条目已离开当前工作区。");
            var snapshot = new Snapshot();
            if (entities.Any(e => string.IsNullOrWhiteSpace(e.SourceFilePath) || string.IsNullOrWhiteSpace(e.Handle)))
                throw new InvalidDataException("校对条目缺少来源图纸或句柄，未保存。");
            foreach (var group in entities.GroupBy(e => Path.GetFullPath(e.SourceFilePath), StringComparer.OrdinalIgnoreCase))
            {
                if (group.Select(e => e.Handle).Distinct(StringComparer.Ordinal).Count() != group.Count())
                    throw new InvalidDataException("同一图纸包含重复句柄，未保存。");
                var drawing = new Drawing { Path = group.Key, Sha256 = Fingerprint(group.Key) };
                drawing.Entries = group.Select(e => new Entry {
                    Handle = e.Handle, PlainText = e.PlainText, RawText = e.RawText, EntityType = e.EntityType,
                    TranslatedText = e.TranslatedText ?? "", GlossaryHit = e.GlossaryHit, Notes = e.Notes ?? "",
                    Status = dirty.Contains(e) ? (string.IsNullOrWhiteSpace(e.TranslatedText) ? TranslationStatus.Pending : TranslationStatus.Reviewed) : e.Status
                }).ToList();
                snapshot.Drawings.Add(drawing);
            }
            Write(snapshot);
        }
    }

    public void Clear()
    {
        lock (Gate) { _ = Read(); Write(new Snapshot()); }
    }

    public Restoration Restore(Func<string, List<TextEntity>> extract)
    {
        Snapshot snapshot;
        lock (Gate) snapshot = Read();
        var result = new Restoration();
        foreach (var drawing in snapshot.Drawings)
        {
            try
            {
                if (Fingerprint(drawing.Path) != drawing.Sha256) { result.SkippedSources.Add(drawing.Path); continue; }
                var fresh = extract(drawing.Path);
                if (Fingerprint(drawing.Path) != drawing.Sha256) { result.SkippedSources.Add(drawing.Path); continue; }
                var byHandle = fresh.ToLookup(e => e.Handle, StringComparer.Ordinal);
                // Validate the entire drawing before changing any entity; always keep freshly read geometry.
                if (fresh.Count != drawing.Entries.Count || drawing.Entries.Any(p => byHandle[p.Handle].Count() != 1
                    || byHandle[p.Handle].Single().PlainText != p.PlainText || byHandle[p.Handle].Single().RawText != p.RawText
                    || byHandle[p.Handle].Single().EntityType != p.EntityType))
                { result.SkippedSources.Add(drawing.Path); continue; }
                foreach (var p in drawing.Entries)
                {
                    var e = byHandle[p.Handle].Single();
                    e.SourceFilePath = drawing.Path;
                    e.TranslatedText = p.TranslatedText; e.Status = p.Status; e.GlossaryHit = p.GlossaryHit; e.Notes = p.Notes;
                }
                result.Entities.AddRange(fresh);
                result.Sources.Add(drawing.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result.SkippedSources.Add(drawing.Path); }
        }
        return result;
    }

    private static string Fingerprint(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private void Write(Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, snapshot, Options); output.Flush(flushToDisk: true); }
            SafeFileCommit.Commit(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
