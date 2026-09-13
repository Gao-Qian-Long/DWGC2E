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
    public void Dispose() => Directory.Delete(directory, true);
}
