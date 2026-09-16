using DwgTranslator.Core.Tasks;
namespace DwgTranslator.Core.Tests;
public sealed class TaskRecoveryDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-recovery-notice-" + Guid.NewGuid().ToString("N"));
    public TaskRecoveryDiagnosticsTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("broken json")]
    [InlineData("[null]")]
    [InlineData("[{\"Id\":\"bad\",\"FilePath\":\"a.dwg\",\"Status\":999}]")]
    [InlineData("[{\"Id\":\"bad\",\"FilePath\":\"a.dwg\",\"SuccessfulTranslations\":null}]")]
    public void IncompleteRecoveryRemainsVisibleEvenAfterSafeSave(string contents)
    {
        var path = Path.Combine(root, "tasks.json");
        File.WriteAllText(path, contents);
        var store = new JsonTaskStore(path);
        var loaded = store.Load();
        Assert.Contains(root, ((ITaskRecoveryDiagnostics)store).RecoveryWarning);
        store.Save(loaded);
        Assert.False(store.LastSaveFailed);
        Assert.NotNull(store.RecoveryWarning);
        Assert.Equal(contents, File.ReadAllText(store.RecoveryFilePath!));
        var healthy = new JsonTaskStore(path);
        healthy.Load();
        Assert.Null(healthy.RecoveryWarning);
    }
    [Fact]
    public void MissingOrHealthyStateDoesNotDisplayFalseWarning()
    {
        var store = new JsonTaskStore(Path.Combine(root, "tasks.json"));
        store.Load(); Assert.Null(store.RecoveryWarning);
        store.Save(new[] { new TranslationTask("a.dwg") });
        store.Load(); Assert.Null(store.RecoveryWarning);
    }
    public void Dispose() => Directory.Delete(root, true);
}
