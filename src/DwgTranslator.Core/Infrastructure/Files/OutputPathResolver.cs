using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DwgTranslator.Core.Models;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>输出路径的处理结果分类。</summary>
public enum DuplicateResolution
{
    /// <summary>直接可用（目标文件不存在）。</summary>
    UseAsIs,
    /// <summary>目标已存在，按 rename 策略自动加了序号。</summary>
    Renamed,
    /// <summary>目标已存在，按 skip 策略跳过该文件。</summary>
    Skip,
    /// <summary>用户显式允许覆盖既有输出。</summary>
    OverwriteExisting,
    /// <summary>路径无法处理（权限/长路径/源文件路径异常）。</summary>
    Error
}

public sealed class OutputPathResult
{
    public string SourcePath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public bool ShouldSkip { get; set; }
    public string? BackupPath { get; set; }
    public DuplicateResolution Resolution { get; set; }
    /// <summary>给界面看的中文说明，例如「输出文件已存在，按规则跳过」。</summary>
    public string Reason { get; set; } = string.Empty;

    public override string ToString() => $"{Path.GetFileName(SourcePath)} → {Path.GetFileName(OutputPath)} [{Resolution}] {Reason}";
}

/// <summary>
/// 输出路径规划与文件安全（提示词 §27「不要覆盖原文件」）。
///
/// 三条硬规则：
///   ① 默认绝不返回与源文件相同的路径（即使命名规则恰好算成同名也要改）；
///   ② 重名默认 rename（自动加序号），overwrite 必须用户显式选择；
///   ③ 批量场景一次性规划，同时预留「源文件路径」与「已分配目标路径」，
///      避免 A.dwg 的输出路径正好是 B.dwg 的源路径而把别人覆盖掉。
///
/// 纯逻辑（除文件存在性检查外不做 I/O），便于单元测试。
/// </summary>
public sealed class OutputPathResolver
{
    private readonly AppConfig _config;

    public OutputPathResolver(AppConfig config)
    {
        _config = config ?? new AppConfig();
    }

    /// <summary>渲染文件命名规则；支持 {name} {lang} {date} 变量，未知变量原样保留并记日志。</summary>
    public string RenderFileName(string sourceFileName, string targetLangCode, DateTime now)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFileName);
        var extension = Path.GetExtension(sourceFileName);
        var lang = (targetLangCode ?? string.Empty).Trim().ToLowerInvariant();
        var pattern = string.IsNullOrWhiteSpace(_config.OutputNamingPattern) ? "{name}_{lang}" : _config.OutputNamingPattern;

        var rendered = pattern
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{lang}", lang, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);

        // {lang} 为空时会产生 "name_" 这类尾巴，清理掉前后多余分隔符
        rendered = rendered.Trim().TrimEnd('_', '-', ' ', '.');

        foreach (var unknown in FindUnknownTokens(pattern))
            Log.Warning("Output naming pattern contains unknown token {Token}; kept as-is", unknown);

        if (rendered.Length == 0) rendered = name;
        return rendered + extension;
    }

    /// <summary>单个文件的输出路径规划。</summary>
    public OutputPathResult Resolve(string sourcePath, string targetLangCode)
    {
        var batch = ResolveBatch(new[] { sourcePath }, targetLangCode);
        return batch.TryGetValue(SafeFullPath(sourcePath), out var result)
            ? result
            : new OutputPathResult
            {
                SourcePath = sourcePath,
                OutputPath = sourcePath,
                Resolution = DuplicateResolution.Error,
                ShouldSkip = true,
                Reason = "无法计算输出路径（源文件路径无效）"
            };
    }

    /// <summary>
    /// 批量规划：先把所有源文件路径登记为"已占用"，再分配目标名。
    /// 返回字典的键是源文件绝对路径（与传入值一致）。
    /// </summary>
    public IReadOnlyDictionary<string, OutputPathResult> ResolveBatch(IEnumerable<string> sourcePaths, string targetLangCode)
    {
        var results = new Dictionary<string, OutputPathResult>(StringComparer.OrdinalIgnoreCase);
        var paths = (sourcePaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0) return results;

        // ① 源文件路径全部登记为已占用（大小写不敏感）
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var full = SafeFullPath(path);
            if (full.Length > 0) occupied.Add(full);
        }

        var now = DateTime.Now;
        foreach (var path in paths)
        {
            var result = PlanOne(path, targetLangCode, now, occupied);
            results[SafeFullPath(path)] = result;
        }

        return results;
    }

    private OutputPathResult PlanOne(string sourcePath, string targetLangCode, DateTime now, HashSet<string> occupied)
    {
        var result = new OutputPathResult { SourcePath = sourcePath };

        string sourceFull;
        try
        {
            sourceFull = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Invalid source path for output planning: {Path}", sourcePath);
            result.Resolution = DuplicateResolution.Error;
            result.OutputPath = sourcePath;
            result.ShouldSkip = true;
            result.Reason = "源文件路径无效，无法规划输出路径";
            return result;
        }

        string directory;
        try
        {
            var exportDirectory = _config.ExportDirectory ?? string.Empty;
            directory = Path.IsPathRooted(exportDirectory)
                ? exportDirectory
                : Path.Combine(Path.GetDirectoryName(sourceFull) ?? ".", exportDirectory);
            directory = Path.GetFullPath(directory);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Invalid export directory {Dir}; falling back to source folder", _config.ExportDirectory);
            directory = Path.GetDirectoryName(sourceFull) ?? ".";
            result.Reason = "输出目录无效，已改为源文件所在目录；";
        }

        var fileName = RenderFileName(Path.GetFileName(sourceFull), targetLangCode, now);

        // ② 绝不与源文件同名
        var candidate = Path.Combine(directory, fileName);
        if (string.Equals(SafeFullPath(candidate), sourceFull, StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            candidate = Path.Combine(directory, $"{stem}_translated{ext}");
            result.Reason += "命名规则与源文件同名，已自动改名以避免覆盖源文件；";
        }

        // ③ 与已分配/已存在的路径冲突处理
        var exists = occupied.Contains(SafeFullPath(candidate)) || SafeFileExists(candidate);
        switch (NormalizedPolicy())
        {
            case "overwrite":
                if (occupied.Contains(SafeFullPath(candidate)))
                    return Reject(result, candidate, "输出路径属于源文件或本批次已分配路径，拒绝覆盖");
                result.Resolution = DuplicateResolution.OverwriteExisting;
                result.OutputPath = candidate;
                if (exists) result.Reason += "输出文件已存在，按用户设置覆盖；";
                break;

            case "rename":
                var unique = MakeUniquePath(candidate, occupied);
                if (unique is null)
                    return Reject(result, candidate, "输出重名序号已耗尽，拒绝使用冲突路径");
                candidate = unique;
                result.Resolution = exists ? DuplicateResolution.Renamed : DuplicateResolution.UseAsIs;
                result.OutputPath = candidate;
                if (exists) result.Reason += "输出文件已存在，已自动加序号；";
                break;

            default: // skip
                if (exists)
                {
                    result.ShouldSkip = true;
                    result.Resolution = DuplicateResolution.Skip;
                    result.OutputPath = candidate;
                    result.Reason += "输出文件已存在，按规则跳过；";
                }
                else
                {
                    result.Resolution = DuplicateResolution.UseAsIs;
                    result.OutputPath = candidate;
                }
                break;
        }

        if (!result.ShouldSkip && !string.Equals(Path.GetDirectoryName(candidate), Path.GetDirectoryName(sourceFull), StringComparison.OrdinalIgnoreCase))
            result.Reason += $"输出到 {Path.GetDirectoryName(candidate)}；";

        if (_config.BackupSourceBeforeWrite)
        {
            result.BackupPath = BuildBackupPath(sourceFull, occupied);
            if (result.BackupPath is null)
                return Reject(result, candidate, "源文件备份路径已耗尽，拒绝继续");
            if (!result.ShouldSkip) occupied.Add(SafeFullPath(result.BackupPath));
            result.Reason += "写回前将备份源文件；";
        }

        if (!result.ShouldSkip) occupied.Add(SafeFullPath(result.OutputPath));
        result.Reason = result.Reason.TrimEnd('；');
        return result;
    }

    private static OutputPathResult Reject(OutputPathResult result, string candidate, string reason)
    {
        result.OutputPath = candidate;
        result.ShouldSkip = true;
        result.Resolution = DuplicateResolution.Error;
        result.Reason += reason;
        return result;
    }

    private string NormalizedPolicy()
    {
        var policy = (_config.DuplicatePolicy ?? "rename").Trim().ToLowerInvariant();
        return policy is "overwrite" or "skip" ? policy : "rename";
    }

    private static string? MakeUniquePath(string path, HashSet<string> occupied)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!occupied.Contains(SafeFullPath(candidate)) && !SafeFileExists(candidate)) return candidate;
        }
        return null; // Fail closed when all numbered paths are occupied.
    }

    /// <summary>
    /// 源图备份路径：同一张图纸永远对应同一个名字（<c>x.dwg.bak</c>）。
    /// <para>
    /// 旧实现按"路径不存在才用"递增到 <c>x.dwg.2.bak</c>、<c>.3.bak</c>……：一次失败的导出/重试就会
    /// 规划出一个新名字，几次之后源图旁边堆满整份副本。备份的语义是"这张图写回之前的上一份"，属于
    /// 同一个回滚点，就该复用同一个名字。只有该名字在本批次里已被别的输出占用时才 fail closed。
    /// </para>
    /// </summary>
    private static string? BuildBackupPath(string sourceFull, HashSet<string> occupied) =>
        occupied.Contains(SafeFullPath(sourceFull + ".bak")) ? null : sourceFull + ".bak";

    private static IEnumerable<string> FindUnknownTokens(string pattern)
    {
        int index = 0;
        while ((index = pattern.IndexOf('{', index)) >= 0)
        {
            var end = pattern.IndexOf('}', index + 1);
            if (end < 0) break;
            var token = pattern.Substring(index, end - index + 1);
            if (!token.Equals("{name}", StringComparison.OrdinalIgnoreCase)
                && !token.Equals("{lang}", StringComparison.OrdinalIgnoreCase)
                && !token.Equals("{date}", StringComparison.OrdinalIgnoreCase))
                yield return token;
            index = end + 1;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) { Log.Debug(ex, "File existence check failed for {Path}", path); return false; }
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path ?? string.Empty; }
    }
}
