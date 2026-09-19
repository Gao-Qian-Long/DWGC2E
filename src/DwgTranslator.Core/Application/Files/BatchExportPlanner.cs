using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// 批量导出的路径规划（UI 与回归测试共用）。
///
/// 两套入口：
///   · 旧的无参重载 —— 保持 `_translated` 命名与既有行为，供既有调用方与回归测试使用，不破坏兼容。
///   · 新增的配置驱动重载 —— 走 <see cref="OutputPathResolver"/>，实现
///     `motor_zh.dwg` 命名、重名策略（跳过/改名/覆盖）、源文件保护与备份。
///
/// 关键修复：配置驱动版本会把**所有源文件路径**一并登记为已占用，
/// 因此 A.dwg 的输出永远不会落到 B.dwg 的源路径上（旧实现只预留生成名，存在互相覆盖隐患）。
/// </summary>
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

        // 源文件路径先登记：批量导出绝不允许把某个源文件当成别人的输出目标覆盖掉
        foreach (var source in sourcePaths.Select(Normalize).Where(p => p.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            reserved.Add(source);
        }

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

    // ───────────────────────── 配置驱动（新增） ─────────────────────────

    /// <summary>
    /// 按用户配置规划输出路径：命名规则（默认 `{name}_{lang}` → motor_zh.dwg）、
    /// 重名策略（skip / rename / overwrite）、源文件保护与可选备份。
    /// </summary>
    public static IReadOnlyDictionary<string, OutputPathResult> CreateDestinationMap(
        IEnumerable<string> sourcePaths, string targetLangCode, AppConfig config)
    {
        return new OutputPathResolver(config).ResolveBatch(sourcePaths, targetLangCode);
    }

    /// <summary>单个文件的配置驱动规划（导出单张图纸时使用）。</summary>
    public static OutputPathResult CreateDestination(string sourcePath, string targetLangCode, AppConfig config)
    {
        return new OutputPathResolver(config).Resolve(sourcePath, targetLangCode);
    }

    /// <summary>
    /// 配置驱动的导出计划：可写目标（源文件 → 输出路径）+ 按重名策略被跳过的条目。
    /// 调用方（UI）把用户选定的导出目录写进 config.ExportDirectory 后传进来即可。
    /// </summary>
    public static ExportPlan CreateExportPlan(IEnumerable<string> sourcePaths, string targetLangCode, AppConfig config)
    {
        var plan = new ExportPlan();
        foreach (var pair in new OutputPathResolver(config).ResolveBatch(sourcePaths, targetLangCode))
            plan.Add(pair.Value);
        return plan;
    }
    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return string.Empty; }
    }
}
/// <summary>一次批量导出的路径规划结果。</summary>
public sealed class ExportPlan
{
    /// <summary>兼容视图：源文件路径 → 输出文件路径，派生自 Results，不会与其分歧。</summary>
    public IReadOnlyDictionary<string, string> Destinations => Results.Where(result => !result.ShouldSkip).ToDictionary(result => result.SourcePath, result => result.OutputPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>被跳过的文件及原因（界面提示“已跳过 N 个已存在的输出文件”），同样派生自 Results。</summary>
    public IReadOnlyList<OutputPathResult> Skipped => Results.Where(result => result.ShouldSkip).ToList();
    public List<OutputPathResult> Results { get; } = new();

    /// <summary>Adds the single authoritative result for one source file.</summary>
    internal void Add(OutputPathResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Results.Add(result);
    }
}
