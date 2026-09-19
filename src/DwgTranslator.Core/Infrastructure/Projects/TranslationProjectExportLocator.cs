using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Resolves the most recent exported file for a drawing without making the UI understand
/// the project history format.  Export history is append-only and may contain paths that
/// were later moved or deleted, so callers can ask for an existing path and transparently
/// fall back to an older successful export.
/// </summary>
public static class TranslationProjectExportLocator
{
    /// <summary>
    /// Returns the newest output path recorded for <paramref name="sourcePath"/>.
    /// </summary>
    /// <param name="requireExistingFile">When true, ignores stale history entries and returns
    /// the newest path that still exists on disk.</param>
    public static string? FindLatestOutputPath(
        TranslationProject? project,
        string? sourcePath,
        bool requireExistingFile = true)
    {
        if (project == null || string.IsNullOrWhiteSpace(sourcePath)) return null;

        var normalizedSource = NormalizePath(sourcePath);
        var candidates = project.ExportHistory
            .OrderByDescending(item => item.ExportedAtUtc)
            .SelectMany(item => item.Files ?? new List<TranslationProjectExportFile>())
            .Where(item => string.Equals(NormalizePath(item.SourcePath), normalizedSource,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => NormalizePath(item.OutputPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!requireExistingFile) return candidates.FirstOrDefault();
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path.Trim()); }
        catch { return path.Trim(); }
    }
}
