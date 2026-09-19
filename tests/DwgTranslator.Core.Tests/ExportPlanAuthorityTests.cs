using System;
using System.IO;
using System.Linq;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// ExportPlan 的单一权威来源：Results 是唯一状态，Skipped 与 Destinations 都是派生视图。
/// 这三个集合一旦分歧，批量导出就会出现"界面说跳过了、实际却写了"这类静默错误。
/// </summary>
public class ExportPlanAuthorityTests : IDisposable
{
    private readonly string _root;

    public ExportPlanAuthorityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwgc2e-exportplan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private string CreateFile(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private AppConfig Config(string policy) => new()
    {
        ExportDirectory = _root,
        OutputNamingPattern = "{name}_{lang}",
        DuplicatePolicy = policy
    };

    [Fact]
    public void EverySourceFileHasExactlyOneResultEvenWhenSkipped()
    {
        var source = CreateFile("a.dwg");
        // 预置目标，让 skip 策略命中
        File.WriteAllText(Path.Combine(_root, "a_EN.dwg"), "existing");

        var plan = BatchExportPlanner.CreateExportPlan(new[] { source }, "EN", Config("skip"));

        Assert.Single(plan.Results);
        Assert.Equal(source, plan.Results[0].SourcePath);
        Assert.True(plan.Results[0].ShouldSkip);
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void SkippedAndDestinationsAreDerivedSoTheyCannotDriftFromResults()
    {
        var skipped = CreateFile("skip.dwg");
        File.WriteAllText(Path.Combine(_root, "skip_EN.dwg"), "existing");
        var fresh = CreateFile("fresh.dwg");

        var plan = BatchExportPlanner.CreateExportPlan(new[] { skipped, fresh }, "EN", Config("skip"));

        // 视图必须与权威结果集严格一致
        Assert.Equal(2, plan.Results.Count);
        Assert.Equal(plan.Results.Count(r => r.ShouldSkip), plan.Skipped.Count());
        Assert.Equal(plan.Results.Count(r => !r.ShouldSkip), plan.Destinations.Count);
        Assert.DoesNotContain(skipped, plan.Destinations.Keys);
        Assert.Contains(fresh, plan.Destinations.Keys);
    }

    [Fact]
    public void DerivedViewsAreReadOnlyAndReflectLaterChangesToResults()
    {
        var source = CreateFile("later.dwg");
        var plan = BatchExportPlanner.CreateExportPlan(new[] { source }, "EN", Config("rename"));

        Assert.Empty(plan.Skipped);
        Assert.Single(plan.Destinations);

        // 派生视图每次读取都跟随 Results，不需要（也不允许）另行同步
        plan.Results[0].ShouldSkip = true;
        Assert.Single(plan.Skipped);
        Assert.Empty(plan.Destinations);
    }

    [Fact]
    public void DestinationsNeverPointAtTheSourceDrawing()
    {
        var source = CreateFile("same.dwg");
        var plan = BatchExportPlanner.CreateExportPlan(new[] { source }, "EN", Config("overwrite"));

        foreach (var result in plan.Results.Where(r => !r.ShouldSkip))
        {
            Assert.NotEqual(
                Path.GetFullPath(source),
                Path.GetFullPath(result.OutputPath),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
