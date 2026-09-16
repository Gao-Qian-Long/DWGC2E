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
