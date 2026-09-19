using DwgTranslator.Core.Services;
namespace DwgTranslator.Core.Tests;
public sealed class SafeFileCommitTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dwgc2e-commit-" + Guid.NewGuid().ToString("N"));
    public SafeFileCommitTests() => Directory.CreateDirectory(directory);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingOutputRequiresExplicitOverwrite(bool overwrite)
    {
        var target = Path.Combine(directory, "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(target, "original"); File.WriteAllText(temp, "replacement");
        if (overwrite) SafeFileCommit.Commit(temp, target, true);
        else Assert.Throws<IOException>(() => SafeFileCommit.Commit(temp, target, false));
        Assert.Equal(overwrite ? "replacement" : "original", File.ReadAllText(target));
    }
    [Fact]
    public void EmptyOutputNeverDestroysPreviousResult()
    {
        var target = Path.Combine(directory, "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(target, "original"); File.WriteAllText(temp, "");
        Assert.Throws<IOException>(() => SafeFileCommit.Commit(temp, target, true));
        Assert.Equal("original", File.ReadAllText(target));
    }
    [Fact]
    public void LockedDestinationPreservesPreviousResult()
    {
        var target = Path.Combine(directory, "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(target, "original"); File.WriteAllText(temp, "replacement");
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => SafeFileCommit.Commit(temp, target, true));
        Assert.Equal("original", File.ReadAllText(target));
    }
    [Fact]
    public void OverwriteKeepsTheReplacedOutputAsPreviousRollbackPoint()
    {
        var target = Path.Combine(directory, "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(target, "v1"); File.WriteAllText(temp, "v2");

        var preserved = SafeFileCommit.Commit(temp, target, true);

        // 旧实现把这份旧输出交给操作系统删掉了；现在它必须落在固定名字的回滚点上。
        Assert.Null(preserved);
        Assert.Equal("v2", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void RollbackPointSurvivesWhenTheOverwriteCannotHappen()
    {
        var target = Path.Combine(directory, "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(target, "v1"); File.WriteAllText(temp, "v2");

        // 固定名字被占用时退到后备名字，覆盖照常完成。
        using (var locked = File.Open(target + SafeFileCommit.RollbackSuffix, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
        {
            var preserved = SafeFileCommit.Commit(temp, target, true);
            Assert.True(preserved == null || preserved == target + ".2" + SafeFileCommit.RollbackSuffix,
                "unexpected rollback: " + preserved);
        }

        Assert.Equal("v2", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void LockedDestinationFailsLoudlyAndKeepsBothFiles()
    {
        var target = Path.Combine(directory, "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(target, "v1"); File.WriteAllText(temp, "v2");

        Exception? failure;
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
            failure = Record.Exception(() => SafeFileCommit.Commit(temp, target, true));

        Assert.True(failure is IOException, "unexpected: " + (failure?.GetType().FullName ?? "no exception")
            + " | files=" + string.Join(",", Directory.GetFiles(directory).Select(Path.GetFileName)));
        Assert.Contains("out.dwg", failure!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("v1", File.ReadAllText(target));
        Assert.Equal("v2", File.ReadAllText(temp));
    }
    [Fact]
    public void OverwriteRemovesTheRollbackPointOnceTheNewOutputIsDurable()
    {
        var target = Path.Combine(directory, "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(target, "v1"); File.WriteAllText(temp, "v2");

        Assert.Null(SafeFileCommit.Commit(temp, target, true));

        Assert.Equal("v2", File.ReadAllText(target));
        Assert.False(File.Exists(target + SafeFileCommit.RollbackSuffix));
        Assert.False(File.Exists(temp));
    }
    [Fact]
    public void StaleRollbackPointDoesNotBlockTheNextOverwrite()
    {
        var target = Path.Combine(directory, "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(target, "v2"); File.WriteAllText(temp, "v3");
        File.WriteAllText(target + SafeFileCommit.RollbackSuffix, "crashed-run-left-this");

        // 上一次崩溃留下的 .previous 会被本次替换接管，不会被迫改用第二个回滚点名字。
        Assert.Null(SafeFileCommit.Commit(temp, target, true));

        Assert.Equal("v3", File.ReadAllText(target));
        Assert.False(File.Exists(target + SafeFileCommit.RollbackSuffix));
        Assert.False(File.Exists(target + ".2" + SafeFileCommit.RollbackSuffix));
    }
    [Fact]
    public void CommittingIntoAMissingDirectoryStillFailsClosedWithoutLosingTheOutput()
    {
        var target = Path.Combine(directory, "missing-dir", "out.dwg"); var temp = Path.Combine(directory, "new.tmp");
        File.WriteAllText(temp, "replacement");

        Assert.ThrowsAny<IOException>(() => SafeFileCommit.Commit(temp, target, true));

        Assert.False(File.Exists(target));
        Assert.Equal("replacement", File.ReadAllText(temp));
    }
    [Fact]
    public async Task ConcurrentNonOverwriteCommitsHaveExactlyOneWinner()
    {
        var target = Path.Combine(directory, "race.dwg");
        var inputs = Enumerable.Range(0, 12).Select(i => Path.Combine(directory, $"candidate-{i}.tmp")).ToArray();
        for (var i = 0; i < inputs.Length; i++) File.WriteAllText(inputs[i], $"drawing-{i}");
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = inputs.Select((input, index) => Task.Run(async () =>
        {
            await start.Task;
            try { SafeFileCommit.Commit(input, target, false); return (Index: index, Won: true); }
            catch (IOException) { return (Index: index, Won: false); }
        })).ToArray();
        start.SetResult(true);
        var results = await Task.WhenAll(attempts);
        var winner = Assert.Single(results.Where(result => result.Won));
        Assert.Equal($"drawing-{winner.Index}", File.ReadAllText(target));
        Assert.False(File.Exists(inputs[winner.Index]));
        foreach (var loser in results.Where(result => !result.Won))
            Assert.Equal($"drawing-{loser.Index}", File.ReadAllText(inputs[loser.Index]));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
