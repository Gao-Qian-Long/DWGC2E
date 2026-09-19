using System;
using System.IO;
using System.Linq;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// 文件安全的行为锁：默认不覆盖源文件、重名策略、批量不互相覆盖。
/// 这些规则一旦被改坏会直接毁用户的图纸，必须有测试兜住。
/// </summary>
public class OutputPathResolverTests : IDisposable
{
    private readonly string _root;

    public OutputPathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwgc2e-outpath-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理失败不影响结论 */ }
    }

    private AppConfig Config(string policy = "skip", bool backup = false)
        => new()
        {
            ExportDirectory = "exports",
            OutputNamingPattern = "{name}_{lang}",
            DuplicatePolicy = policy,
            BackupSourceBeforeWrite = backup
        };

    private string CreateFile(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void 默认命名规则生成_原名_语言码()
    {
        var source = CreateFile("motor.dwg");
        var result = new OutputPathResolver(Config()).Resolve(source, "ZH");

        Assert.Equal("motor_zh.dwg", Path.GetFileName(result.OutputPath));
        Assert.False(result.ShouldSkip);
        Assert.Contains("exports", result.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 命名规则与源文件同名时自动改名绝不覆盖源文件()
    {
        // 真正的碰撞场景：命名规则就是 {name}（不带语言后缀）+ 输出到源目录，
        // 此时算出来的目标路径恰好等于源文件本身，必须自动改名而不是覆盖它。
        var source = CreateFile("motor_zh.dwg");
        var config = Config();
        config.ExportDirectory = ".";
        config.OutputNamingPattern = "{name}";

        var result = new OutputPathResolver(config).Resolve(source, "zh");

        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(result.OutputPath));
        Assert.Equal("motor_zh_translated.dwg", Path.GetFileName(result.OutputPath));
        Assert.False(result.ShouldSkip);
    }

    [Fact]
    public void 默认命名规则下语言后缀会正常追加()
    {
        var source = CreateFile("motor_zh.dwg");
        var config = Config();
        config.ExportDirectory = ".";

        var result = new OutputPathResolver(config).Resolve(source, "zh");

        // {name} 取不含扩展名的 motor_zh，再拼 _zh → motor_zh_zh.dwg
        Assert.Equal("motor_zh_zh.dwg", Path.GetFileName(result.OutputPath));
        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(result.OutputPath));
    }

    [Fact]
    public void 输出到源目录时也不会覆盖同名的其它既有文件()
    {
        var source = CreateFile("gear.dwg");
        CreateFile("gear_zh.dwg", "既有输出");

        var config = Config("skip");
        config.ExportDirectory = ".";
        var result = new OutputPathResolver(config).Resolve(source, "zh");

        Assert.True(result.ShouldSkip);
        Assert.Equal("既有输出", File.ReadAllText(Path.Combine(_root, "gear_zh.dwg")));
    }

    [Fact]
    public void skip策略下已存在输出则跳过并给出中文说明()
    {
        var source = CreateFile("pump.dwg");
        CreateFile(Path.Combine("exports", "pump_zh.dwg"), "already here");

        var result = new OutputPathResolver(Config("skip")).Resolve(source, "zh");

        Assert.True(result.ShouldSkip);
        Assert.Equal(DuplicateResolution.Skip, result.Resolution);
        Assert.Contains("跳过", result.Reason);
    }

    [Fact]
    public void rename策略下已存在输出则自动加序号()
    {
        var source = CreateFile("valve.dwg");
        CreateFile(Path.Combine("exports", "valve_zh.dwg"));

        var result = new OutputPathResolver(Config("rename")).Resolve(source, "zh");

        Assert.False(result.ShouldSkip);
        Assert.Equal(DuplicateResolution.Renamed, result.Resolution);
        Assert.Equal("valve_zh (2).dwg", Path.GetFileName(result.OutputPath));
    }

    [Fact]
    public void overwrite策略只在用户显式选择时才覆盖既有输出()
    {
        var source = CreateFile("flange.dwg");
        CreateFile(Path.Combine("exports", "flange_zh.dwg"));

        var result = new OutputPathResolver(Config("overwrite")).Resolve(source, "zh");

        Assert.False(result.ShouldSkip);
        Assert.Equal(DuplicateResolution.OverwriteExisting, result.Resolution);
        Assert.Equal("flange_zh.dwg", Path.GetFileName(result.OutputPath));
    }

    [Fact]
    public void 开启备份时返回源文件备份路径()
    {
        var source = CreateFile("shaft.dwg");
        var result = new OutputPathResolver(Config(backup: true)).Resolve(source, "zh");

        Assert.NotNull(result.BackupPath);
        Assert.EndsWith(".bak", result.BackupPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("备份", result.Reason);
    }

    [Fact]
    public void 批量规划不会让一个文件覆盖另一个文件的源文件()
    {
        // A.dwg 的目标名恰好是 B.dwg 的源文件名：A_zh.dwg
        var a = CreateFile("A.dwg");
        var b = CreateFile("A_zh.dwg");

        var config = Config("rename");
        config.ExportDirectory = "."; // Target must share the source directory to exercise the collision.
        var results = new OutputPathResolver(config).ResolveBatch(new[] { a, b }, "zh");

        var outputA = results[Path.GetFullPath(a)].OutputPath;
        var outputB = results[Path.GetFullPath(b)].OutputPath;

        Assert.NotEqual(Path.GetFullPath(b), Path.GetFullPath(outputA));   // 关键：不得等于 B 的源文件
        Assert.NotEqual(Path.GetFullPath(outputA), Path.GetFullPath(outputB)); // 目标之间也不能相同
    }

    [Fact]
    public void 未知变量原样保留且不崩溃()
    {
        var source = CreateFile("cover.dwg");
        var config = Config();
        config.OutputNamingPattern = "{name}_{lang}_{unknown}";

        var result = new OutputPathResolver(config).Resolve(source, "en");

        Assert.Contains("{unknown}", Path.GetFileName(result.OutputPath));
        Assert.EndsWith(".dwg", result.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 语言码为空时仍能给出合法文件名()
    {
        var source = CreateFile("base.dwg");
        var result = new OutputPathResolver(Config()).Resolve(source, string.Empty);

        Assert.False(string.IsNullOrWhiteSpace(Path.GetFileName(result.OutputPath)));
        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(result.OutputPath));
    }

    [Fact]
    public void OverwriteCannotTargetAnotherBatchSource()
    {
        var a = CreateFile("A.dwg", "source-a");
        var b = CreateFile("A_zh.dwg", "source-b");
        var config = Config("overwrite");
        config.ExportDirectory = ".";
        var result = new OutputPathResolver(config).ResolveBatch(new[] { a, b }, "zh")[a];
        Assert.True(result.ShouldSkip);
        Assert.Equal(DuplicateResolution.Error, result.Resolution);
        Assert.Equal("source-a", File.ReadAllText(a));
        Assert.Equal("source-b", File.ReadAllText(b));
    }

    [Fact]
    public void OverwriteCannotReuseAssignedBatchOutput()
    {
        var a = CreateFile("a.dwg"); var b = CreateFile("b.dwg");
        var config = Config("overwrite"); config.OutputNamingPattern = "shared";
        var results = new OutputPathResolver(config).ResolveBatch(new[] { a, b }, "zh");
        Assert.False(results[a].ShouldSkip);
        Assert.True(results[b].ShouldSkip);
    }

    [Fact]
    public void RenameExhaustionFailsClosed()
    {
        var source = CreateFile("full.dwg"); var config = Config("rename");
        config.ExportDirectory = ".";
        CreateFile("full_zh.dwg", "original-output");
        for (var i = 2; i < 1000; i++) CreateFile($"full_zh ({i}).dwg");
        var result = new OutputPathResolver(config).Resolve(source, "zh");
        Assert.True(result.ShouldSkip);
        Assert.Equal(DuplicateResolution.Error, result.Resolution);
        Assert.Equal("original-output", File.ReadAllText(Path.Combine(_root, "full_zh.dwg")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupPathStaysTheSameNameAcrossRepeatedFailures(bool overwriteSource)
    {
        var source = CreateFile("backup.dwg");
        var config = Config("overwrite", backup: true);
        if (overwriteSource) { config.ExportDirectory = "."; config.OutputNamingPattern = "{name}"; }
        CreateFile("backup.dwg.bak", "original-backup");
        var resolver = new OutputPathResolver(config);

        // 不因"已有同名备份"就换名字：连续三次失败/重试仍然落在同一个回滚点上。
        var first = resolver.Resolve(source, "zh");
        var second = resolver.Resolve(source, "zh");
        var third = resolver.Resolve(source, "zh");

        Assert.False(first.ShouldSkip);
        Assert.Equal(source + ".bak", first.BackupPath);
        Assert.Equal(first.BackupPath, second.BackupPath);
        Assert.Equal(first.BackupPath, third.BackupPath);
        Assert.Equal("original-backup", File.ReadAllText(source + ".bak")); // 规划不写盘
        Assert.False(File.Exists(source + ".2.bak"));
    }

    [Fact]
    public void BackupPathCollisionWithPlannedOutputFailsClosed()
    {
        var a = CreateFile("a.dwg");
        var b = CreateFile("a.dwg.bak"); // 另一个待翻译的图纸恰好占用了备份名字
        var config = Config("overwrite", backup: true);
        var results = new OutputPathResolver(config).ResolveBatch(new[] { a, b }, "zh");

        Assert.True(results[a].ShouldSkip);
        Assert.Equal(DuplicateResolution.Error, results[a].Resolution);
        Assert.Null(results[a].BackupPath);
        Assert.Equal("x", File.ReadAllText(b)); // 规划不写盘，占位文件保持原样
    }

    [Fact]
    public void ExplicitOwnSourceOverwriteIsAlwaysRenamedEvenWhenBackupEnabled()
    {
        var source = CreateFile("own.dwg");
        var config = Config("overwrite", backup: true);
        config.ExportDirectory = "."; config.OutputNamingPattern = "{name}";
        var result = new OutputPathResolver(config).Resolve(source, "zh");
        Assert.False(result.ShouldSkip);
        Assert.NotEqual(source, result.OutputPath);
        Assert.Equal(Path.Combine(_root, "own_translated.dwg"), result.OutputPath);
        Assert.Equal(source + ".bak", result.BackupPath);
    }
}
