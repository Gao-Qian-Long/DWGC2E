using DwgTranslator.Core.Api;

namespace DwgTranslator.Core.Services;

public enum GlossaryMergeChoice { Local, Remote }

/// <summary>One ephemeral comparison row. Missing entries represent deletion.</summary>
public sealed class GlossaryMergeRow
{
    public CloudGlossaryEntry? Basis { get; }
    public CloudGlossaryEntry? Local { get; }
    public CloudGlossaryEntry? Remote { get; }
    public GlossaryMergeChoice? SuggestedChoice { get; }
    public bool HasConflict => SuggestedChoice == null;
    internal GlossaryMergeRow(CloudGlossaryEntry? basis, CloudGlossaryEntry? local, CloudGlossaryEntry? remote)
    {
        Basis = basis; Local = local; Remote = remote;
        SuggestedChoice = CloudGlossaryMerge.Equal(local, remote) || CloudGlossaryMerge.Equal(local, basis)
            ? GlossaryMergeChoice.Remote : CloudGlossaryMerge.Equal(remote, basis) ? GlossaryMergeChoice.Local : null;
    }
}

/// <summary>Pure three-way comparison; no disk, network, revision refresh, or automatic cloud writes.</summary>
public static class CloudGlossaryMerge
{
    public static bool Equal(CloudGlossaryEntry? a, CloudGlossaryEntry? b) =>
        a == null || b == null ? a == b : a.Source == b.Source && a.Target == b.Target &&
        (a.Note ?? "") == (b.Note ?? "") && (a.Category ?? "") == (b.Category ?? "") &&
        (a.Folder ?? "") == (b.Folder ?? "") && a.Enabled == b.Enabled;

    private static string? Identity(CloudGlossaryEntry entry) =>
        Guid.TryParseExact(entry.Id, "D", out var id) ? id.ToString("D") : null;

    private static CloudGlossaryEntry Copy(CloudGlossaryEntry entry) => new()
    {
        Id = entry.Id, Source = entry.Source, Target = entry.Target, Note = entry.Note,
        Category = entry.Category, Folder = entry.Folder, Enabled = entry.Enabled
    };

    private static List<CloudGlossaryEntry> Validate(IReadOnlyList<CloudGlossaryEntry> entries)
    {
        if (entries.Count > 1000) throw new InvalidOperationException("词库超过1000条，未合并。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target))
                throw new InvalidOperationException("词条原文或译文为空，请先核对。");
            var id = Identity(entry);
            if (id != null && !ids.Add(id)) throw new InvalidOperationException("词条标识重复，无法安全匹配。");
        }
        return entries.Select(Copy).ToList();
    }

    public static IReadOnlyList<GlossaryMergeRow> Plan(IReadOnlyList<CloudGlossaryEntry> basis,
        IReadOnlyList<CloudGlossaryEntry> local, IReadOnlyList<CloudGlossaryEntry> remote)
    {
        var b = Validate(basis); var l = Validate(local); var r = Validate(remote);
        var usedLocal = new HashSet<CloudGlossaryEntry>(); var usedRemote = new HashSet<CloudGlossaryEntry>();
        var rows = new List<GlossaryMergeRow>();
        CloudGlossaryEntry? Match(CloudGlossaryEntry entry, List<CloudGlossaryEntry> list, HashSet<CloudGlossaryEntry> used)
        {
            var id = Identity(entry);
            var candidates = id == null ? new List<CloudGlossaryEntry>() : list.Where(x => !used.Contains(x) && Identity(x) == id).ToList();
            // Different stable IDs are different entries, even when their source text is equal.
            if (candidates.Count == 0) candidates = list.Where(x => !used.Contains(x) &&
                (id == null || Identity(x) == null) && x.Source == entry.Source).ToList();
            if (candidates.Count > 1) throw new InvalidOperationException("相同原文对应多个词条，无法安全匹配，请先整理。");
            var result = candidates.SingleOrDefault(); if (result != null) used.Add(result); return result;
        }
        foreach (var entry in b) rows.Add(new(entry, Match(entry, l, usedLocal), Match(entry, r, usedRemote)));
        foreach (var entry in l.Where(x => !usedLocal.Contains(x))) rows.Add(new(null, entry, Match(entry, r, usedRemote)));
        foreach (var entry in r.Where(x => !usedRemote.Contains(x))) rows.Add(new(null, null, entry));
        return rows.AsReadOnly();
    }

    public static IReadOnlyList<CloudGlossaryEntry> Resolve(IReadOnlyList<GlossaryMergeRow> rows,
        IReadOnlyDictionary<int, GlossaryMergeChoice>? choices = null)
    {
        var result = new List<CloudGlossaryEntry>();
        var pairs = new HashSet<(string, string)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var choice = choices != null && choices.TryGetValue(i, out var selected) ? selected : row.SuggestedChoice;
            if (choice is not (GlossaryMergeChoice.Local or GlossaryMergeChoice.Remote))
                throw new InvalidOperationException("请为每个冲突选择本机或最新云端。");
            var entry = choice == GlossaryMergeChoice.Local ? row.Local : row.Remote;
            if (entry == null) continue;
            var copy = Copy(entry);
            // A cloud-deleted row is a new entry when restored, not a stale server identity.
            copy.Id = row.Remote == null ? null : Identity(row.Remote);
            if (!pairs.Add((copy.Source.Trim(), copy.Target.Trim())))
                throw new InvalidOperationException("合并后存在重复原文和译文，请重新选择。");
            result.Add(copy);
        }
        if (result.Count > 1000) throw new InvalidOperationException("合并后超过1000条，请减少词条后重试。");
        return result.AsReadOnly();
    }
}
