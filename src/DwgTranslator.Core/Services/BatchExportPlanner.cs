using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>Pure path planning shared by the UI and regression tests.</summary>
public static class BatchExportPlanner
{
    public static IReadOnlyList<string> GetKnownSources(IEnumerable<TextEntity> entities) => entities
        .Select(e => Normalize(e.SourceFilePath))
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static string CreateDestinationPath(string sourcePath, string targetFolder)
    {
        var source = Path.GetFullPath(sourcePath);
        var folder = Path.GetFullPath(targetFolder);
        return Path.Combine(folder,
            Path.GetFileNameWithoutExtension(source) + "_translated" + Path.GetExtension(source));
    }

    /// <summary>
    /// Creates a collision-free destination for every source. Files with the same base name from
    /// different folders receive a numeric suffix instead of overwriting each other.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CreateDestinationMap(
        IEnumerable<string> sourcePaths, string targetFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourcePaths.Select(Normalize).Where(p => p.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = CreateDestinationPath(source, targetFolder);
            var stem = Path.Combine(Path.GetDirectoryName(candidate)!, Path.GetFileNameWithoutExtension(candidate));
            var extension = Path.GetExtension(candidate);
            var suffix = 2;
            while (!reserved.Add(candidate))
                candidate = $"{stem}_{suffix++}{extension}";
            result[source] = candidate;
        }
        return result;
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return string.Empty; }
    }
}
