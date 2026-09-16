using System.Diagnostics;
using DwgTranslator.Core.Tasks;

namespace DwgTranslator.Core.Tests;

public sealed class TaskTemporaryFileTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dwgc2e-temp-policy-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(folder, "tasks.json");
    public TaskTemporaryFileTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private string Candidate(bool alive = false, bool old = true)
    {
        using var process = Process.GetCurrentProcess();
        // Same PID, different creation time models a recycled PID; the current owner
        // is positively identified and must be preserved when its time matches.
        var start = process.StartTime.ToUniversalTime().Ticks - (alive ? 0 : 1);
        var path = StorePath + $".owner-{process.Id}-{start}-{Guid.NewGuid():N}.tmp";
        File.WriteAllText(path, "uncommitted task data");
        if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        return path;
    }
    private JsonTaskStore Save()
    {
        var store = new JsonTaskStore(StorePath);
        store.Save(new[] { new TranslationTask("latest.dwg") });
        Assert.False(store.LastSaveFailed);
        Assert.Equal("latest.dwg", Assert.Single(store.Load()).FilePath);
        return store;
    }
    [Fact] public void SupersededOldCandidateWithEndedOwnerIsRemoved() { var path = Candidate(); Save(); Assert.False(File.Exists(path)); }
    [Fact] public void ActiveOwnerIsPreservedEvenIfTimestampIsOld() { var path = Candidate(alive: true); Save(); Assert.True(File.Exists(path)); }
    [Fact] public void RecentCandidateIsPreserved() { var path = Candidate(old: false); Save(); Assert.True(File.Exists(path)); }
    [Fact] public void FutureTimestampIsPreserved() { var path = Candidate(); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(1)); Save(); Assert.True(File.Exists(path)); }
    [Fact] public void LockedCandidateDoesNotFailCommitAndNextSaveCleansIt()
    {
        var path = Candidate();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { Save(); Assert.True(File.Exists(path)); }
        Save(); Assert.False(File.Exists(path));
    }
    [Fact] public void ReadOnlyCandidateIsPreserved()
    {
        var path = Candidate(); File.SetAttributes(path, FileAttributes.ReadOnly);
        try { Save(); Assert.True(File.Exists(path)); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
    [Fact(Skip = "Windows host lacks symbolic-link privilege (Win32 1314); run on a link-enabled host before claiming symlink runtime acceptance.")] public void CleanupDoesNotFollowSymbolicLinkCandidates()
    {
        var target = Path.Combine(folder, "user-evidence.txt"); File.WriteAllText(target, "keep original");
        var link = Candidate(); File.Delete(link);
        File.CreateSymbolicLink(link, target);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-2));
        Save(); Assert.Equal("keep original", File.ReadAllText(target)); Assert.True(File.Exists(link));
    }
    [Fact] public void FailedCommitNeverCleansCandidates()
    {
        Save(); var path = Candidate();
        using (var locked = new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var store = new JsonTaskStore(StorePath); store.Save(Array.Empty<TranslationTask>());
            Assert.True(store.LastSaveFailed); Assert.True(File.Exists(path));
        }
    }
    [Fact] public void CorruptStoreRecoveryKeepsAllUncommittedEvidence()
    {
        File.WriteAllText(StorePath, "broken"); var path = Candidate();
        var store = new JsonTaskStore(StorePath); Assert.Empty(store.Load()); store.Save(Array.Empty<TranslationTask>());
        Assert.False(store.LastSaveFailed); Assert.True(File.Exists(path)); Assert.Equal("broken", File.ReadAllText(store.RecoveryFilePath!));
    }
    [Fact] public void CleanupIsBoundedAndSubsequentSavesContinue()
    {
        var files = Enumerable.Range(0, 40).Select(_ => Candidate()).ToArray();
        Save(); Assert.Equal(24, files.Count(File.Exists)); Save(); Assert.Equal(8, files.Count(File.Exists)); Save(); Assert.DoesNotContain(files, File.Exists);
    }
    [Fact] public void LegacyRecoveryMalformedAndOtherWorkspaceFilesArePreserved()
    {
        var names = new[] { "tasks.json." + Guid.NewGuid().ToString("N") + ".tmp", "tasks.json.recovery-" + Guid.NewGuid().ToString("N") + ".json", "tasks.json.owner-0-1-" + Guid.NewGuid().ToString("N") + ".tmp", "tasks.json.owner-1-1-not-guid.tmp", "other.json.owner-1-1-" + Guid.NewGuid().ToString("N") + ".tmp" };
        foreach (var name in names) { File.WriteAllText(Path.Combine(folder, name), name); File.SetLastWriteTimeUtc(Path.Combine(folder, name), DateTime.UtcNow.AddDays(-2)); }
        var nested = Path.Combine(folder, "nested"); Directory.CreateDirectory(nested); var nestedFile = Path.Combine(nested, Path.GetFileName(Candidate())); File.WriteAllText(nestedFile, "nested");
        Save(); foreach (var name in names) Assert.Equal(name, File.ReadAllText(Path.Combine(folder, name))); Assert.Equal("nested", File.ReadAllText(nestedFile));
    }
}
