using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class ProofreadingStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proofreading-" + Guid.NewGuid().ToString("N"));
    public ProofreadingStoreTests() => Directory.CreateDirectory(root);
    private string Source(string name = "source.dwg") { var p = Path.Combine(root, name); File.WriteAllText(p, "drawing fixture"); return p; }
    private ProofreadingStore Store(string owner = "A") => new(Path.Combine(AccountWorkspace.DirectoryFor(root, owner), "proofreading.json"));
    private static TextEntity Entity(string path, string handle = "A1") => new() { SourceFilePath = path, Handle = handle, PlainText = "阀门", RawText = "阀门", EntityType = "DBText", TranslatedText = "Valve reviewed", Status = TranslationStatus.Translated };

    [Fact]
    public void NewStoreRestoresSavedTextWithFreshGeometryAndNoOriginalMutation()
    {
        var source = Source(); var entity = Entity(source); entity.Position = new Point3d(9, 8, 7);
        Store().Save(new[] { entity }, new[] { entity });
        Assert.Equal(TranslationStatus.Translated, entity.Status);
        entity.TranslatedText = "unsaved subsequent change";
        var restored = Store().Restore(p => new() { new TextEntity { SourceFilePath = p, Handle = "A1", PlainText = "阀门", RawText = "阀门", EntityType = "DBText", Position = new Point3d(1, 2, 3) } });
        var item = Assert.Single(restored.Entities);
        Assert.Equal("Valve reviewed", item.TranslatedText); Assert.Equal(TranslationStatus.Reviewed, item.Status);
        Assert.Equal(1, item.Position.X); Assert.Empty(restored.SkippedSources);
    }
    [Fact]
    public void SameHandleInDifferentDrawingsIsIsolated()
    {
        var a = Entity(Source("a.dwg")); var b = Entity(Source("b.dwg")); b.TranslatedText = "Valve B";
        Store().Save(new[] { a, b }, new[] { a, b });
        var result = Store().Restore(p => new() { Entity(p) });
        Assert.Equal(2, result.Entities.Count); Assert.Equal("Valve B", result.Entities.Single(e => e.SourceFilePath == b.SourceFilePath).TranslatedText);
    }
    [Fact]
    public void AccountsAndGuestCannotReadEachOther()
    {
        var e = Entity(Source()); Store().Save(new[] { e }, new[] { e });
        Assert.Empty(Store("B").Restore(_ => throw new Exception()).Entities);
        Assert.Empty(Store("").Restore(_ => throw new Exception()).Entities);
        Assert.Single(Store().Restore(p => new() { Entity(p) }).Entities);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChangedOrMissingSourceIsSkippedWithoutEditingRecord(bool remove)
    {
        var e = Entity(Source()); var store = Store(); store.Save(new[] { e }, new[] { e }); var before = File.ReadAllBytes(store.FilePath);
        if (remove) File.Delete(e.SourceFilePath); else File.AppendAllText(e.SourceFilePath, "changed");
        var result = store.Restore(_ => throw new Exception("Must not read stale geometry"));
        Assert.Empty(result.Entities); Assert.Single(result.SkippedSources); Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }
    [Theory]
    [InlineData("handle")] [InlineData("plain")] [InlineData("raw")] [InlineData("type")] [InlineData("duplicate")]
    public void ChangedEntityIdentityRejectsEntireDrawing(string mismatch)
    {
        var e = Entity(Source()); var store = Store(); store.Save(new[] { e }, new[] { e });
        var fresh = Entity(e.SourceFilePath); fresh.TranslatedText = "fresh";
        if (mismatch == "handle") fresh.Handle = "A2";
        if (mismatch == "plain") fresh.PlainText = "different";
        if (mismatch == "raw") fresh.RawText = "different";
        if (mismatch == "type") fresh.EntityType = "MText";
        var result = store.Restore(_ => mismatch == "duplicate" ? new() { fresh, fresh } : new() { fresh });
        Assert.Empty(result.Entities); Assert.Single(result.SkippedSources); Assert.Equal("fresh", fresh.TranslatedText);
    }
    [Theory]
    [InlineData("invalid json")] [InlineData("{\"Version\":2,\"Drawings\":[]}")]
    public void CorruptOrFutureRecordIsNeverOverwritten(string content)
    {
        var store = Store(); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!); File.WriteAllText(store.FilePath, content);
        var e = Entity(Source()); Assert.ThrowsAny<Exception>(() => store.Save(new[] { e }, new[] { e }));
        Assert.ThrowsAny<Exception>(() => store.Clear()); Assert.Equal(content, File.ReadAllText(store.FilePath));
    }
    [Fact]
    public void ReplacementFailureKeepsPreviousBytesAndLeavesNoTemporaryFile()
    {
        var e = Entity(Source()); var store = Store(); store.Save(new[] { e }, new[] { e }); var before = File.ReadAllBytes(store.FilePath);
        e.TranslatedText = "new edit";
        using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => store.Save(new[] { e }, new[] { e }));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
        Assert.Equal(TranslationStatus.Translated, e.Status);
        store.Save(new[] { e }, new[] { e }); Assert.Equal("new edit", Assert.Single(Store().Restore(p => new() { Entity(p) }).Entities).TranslatedText);
    }
    [Fact]
    public void BlankEditSurvivesAsPendingAndClearIsDurable()
    {
        var e = Entity(Source()); e.TranslatedText = ""; var store = Store(); store.Save(new[] { e }, new[] { e });
        var restored = Assert.Single(Store().Restore(p => new() { Entity(p) }).Entities);
        Assert.Empty(restored.TranslatedText); Assert.Equal(TranslationStatus.Pending, restored.Status);
        store.Clear(); Assert.Empty(Store().Restore(_ => throw new Exception()).Entities);
    }
    [Fact]
    public void DuplicateHandlesFailBeforeReplacingSavedRecord()
    {
        var e = Entity(Source()); var store = Store(); store.Save(new[] { e }, new[] { e }); var before = File.ReadAllBytes(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.Save(new[] { e, Entity(e.SourceFilePath) }, new[] { e }));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }
    [Fact]
    public void MissingDrawingCannotProduceFalseSaveSuccess()
    {
        var e = Entity(Path.Combine(root, "missing.dwg")); var store = Store();
        Assert.ThrowsAny<IOException>(() => store.Save(new[] { e }, new[] { e })); Assert.False(File.Exists(store.FilePath));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
