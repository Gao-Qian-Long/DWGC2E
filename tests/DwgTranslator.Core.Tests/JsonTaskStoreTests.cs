using DwgTranslator.Core.Tasks;

namespace DwgTranslator.Core.Tests;

public class JsonTaskStoreTests
{
    [Fact]
    public void LockedDestinationPreservesPreviousStateAndNextSaveRecovers()
    {
        var path = Path.Combine(Path.GetTempPath(), "dwgc2e-store-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonTaskStore(path);
            store.Save(new[] { new TranslationTask("original.dwg") });
            Assert.False(store.LastSaveFailed);
            var original = File.ReadAllBytes(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                store.Save(new[] { new TranslationTask("new.dwg") });
                Assert.True(store.LastSaveFailed);
                Assert.Equal(original, File.ReadAllBytes(path));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
            }
            store.Save(new[] { new TranslationTask("new.dwg") });
            Assert.False(store.LastSaveFailed);
            Assert.Equal("new.dwg", Assert.Single(store.Load()).FilePath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidParentReportsFailureWithoutInterruptingCaller()
    {
        var path = Path.Combine(Path.GetTempPath(), "dwgc2e-store-parent-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "not a directory");
        try
        {
            var store = new JsonTaskStore(Path.Combine(path, "tasks.json"));
            store.Save(new[] { new TranslationTask("drawing.dwg") });
            Assert.True(store.LastSaveFailed);
            Assert.Equal("not a directory", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
