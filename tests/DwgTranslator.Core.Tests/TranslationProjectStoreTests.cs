using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class TranslationProjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dwgc2e-project-store-" + Guid.NewGuid().ToString("N"));

    public TranslationProjectStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Source(string account, string name = "drawing.dwg", string content = "dwg-v1")
    {
        var directory = Path.Combine(_root, account, "sources");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static TextEntity Entity(string path, string handle = "1A", string text = "电机") => new()
    {
        SourceFilePath = path, Handle = handle, PlainText = text, RawText = text,
        EntityType = "DBText", TranslatedText = "motor", Status = TranslationStatus.Translated
    };

    [Fact]
    public void CreateLoadListSearchRenameAndAccountIsolation()
    {
        var accountA = Path.Combine(_root, "account-a");
        var accountB = Path.Combine(_root, "account-b");
        var source = Source("account-a", "总图.dwg");
        var store = new TranslationProjectStore(accountA);
        var created = store.Create("泵站项目", "zh", "en", new[] { Entity(source) });

        var loaded = store.Load(created.Id);
        Assert.Equal("pump station".Replace("pump station", "motor"), loaded.Drawings[0].Entries[0].TranslatedText);
        Assert.Single(store.List("泵站"));
        Assert.Single(store.List("总图"));
        Assert.Empty(store.List("不存在"));
        Assert.Empty(new TranslationProjectStore(accountB).List());

        store.Rename(created.Id, "泵站校对版");
        Assert.Equal("泵站校对版", store.Load(created.Id).Name);
    }

    [Fact]
    public void SaveAtomicallyReplacesExistingProjectAndLeavesNoTemporaryFiles()
    {
        var account = Path.Combine(_root, "atomic");
        var source = Source("atomic");
        var store = new TranslationProjectStore(account);
        var project = store.Create("初稿", "zh", "en", new[] { Entity(source) });
        project.Name = "终稿";
        project.Drawings[0].Entries[0].TranslatedText = "final motor";
        store.Save(project);

        Assert.Equal("终稿", store.Load(project.Id).Name);
        Assert.Equal("final motor", store.Load(project.Id).Drawings[0].Entries[0].TranslatedText);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(account, "projects", project.Id), "*.tmp"));
    }

    [Fact]
    public void SourceValidationDetectsMissingAndChangedFiles()
    {
        var account = Path.Combine(_root, "validation");
        var source = Source("validation");
        var store = new TranslationProjectStore(account);
        var project = store.Create("验证", "zh", "en", new[] { Entity(source) });
        var drawing = project.Drawings[0];
        Assert.Equal(ProjectSourceValidation.Valid, store.ValidateSource(drawing));
        File.WriteAllText(source, "changed");
        Assert.Equal(ProjectSourceValidation.Changed, store.ValidateSource(drawing));
        File.Delete(source);
        Assert.Equal(ProjectSourceValidation.Missing, store.ValidateSource(drawing));
    }

    [Fact]
    public void AppendExportPersistsHistory()
    {
        var account = Path.Combine(_root, "history");
        var source = Source("history");
        var store = new TranslationProjectStore(account);
        var project = store.Create("导出", "zh", "en", new[] { Entity(source) });
        store.AppendExport(project.Id, new TranslationProjectExport
        {
            OutputDirectory = Path.Combine(_root, "out"), WritebackMode = "offline",
            Files = { new TranslationProjectExportFile { SourcePath = source, OutputPath = Path.Combine(_root, "out", "drawing_en.dwg"), SuccessCount = 1 } }
        });
        var export = Assert.Single(store.Load(project.Id).ExportHistory);
        Assert.Equal("offline", export.WritebackMode);
        Assert.Equal(1, Assert.Single(export.Files).SuccessCount);
    }

    [Fact]
    public void DuplicateOrEmptyHandlesAreRejected()
    {
        var account = Path.Combine(_root, "handles");
        var source = Source("handles");
        var store = new TranslationProjectStore(account);
        Assert.Throws<InvalidDataException>(() => store.Create("重复", "zh", "en", new[] { Entity(source, "AA"), Entity(source, "AA") }));
        Assert.Throws<InvalidDataException>(() => store.Create("空句柄", "zh", "en", new[] { Entity(source, "") }));
    }

    [Fact]
    public void CorruptAndNewerSchemaProjectsRemainOnDiskButAreExcludedFromList()
    {
        var account = Path.Combine(_root, "corrupt");
        var projects = Path.Combine(account, "projects");
        var corruptDir = Path.Combine(projects, "corrupt");
        var newerDir = Path.Combine(projects, "newer");
        Directory.CreateDirectory(corruptDir); Directory.CreateDirectory(newerDir);
        File.WriteAllText(Path.Combine(corruptDir, "project.json"), "{broken");
        File.WriteAllText(Path.Combine(newerDir, "project.json"), JsonSerializer.Serialize(new TranslationProject { Id = "newer", SchemaVersion = 999 }));
        var store = new TranslationProjectStore(account);

        Assert.Empty(store.List());
        Assert.True(File.Exists(Path.Combine(corruptDir, "project.json")));
        Assert.True(File.Exists(Path.Combine(newerDir, "project.json")));
        Assert.ThrowsAny<Exception>(() => store.Load("corrupt"));
        Assert.Throws<InvalidDataException>(() => store.Load("newer"));
    }
    [Fact]
    public void RevisionIncrementsMonotonicallyOnSuccessfulSaves()
    {
        var account = Path.Combine(_root, "revision");
        var source = Source("revision");
        var store = new TranslationProjectStore(account);
        var project = store.Create("版本", "zh", "en", new[] { Entity(source) });

        Assert.Equal(1, project.Revision);
        project.Name = "版本二";
        store.Save(project);

        Assert.Equal(2, project.Revision);
        Assert.Equal(2, store.Load(project.Id).Revision);
    }

    [Fact]
    public void StaleSaveFromAnotherStoreIsRejectedWithoutOverwritingNewerData()
    {
        var account = Path.Combine(_root, "conflict");
        var source = Source("conflict");
        var firstStore = new TranslationProjectStore(account);
        var created = firstStore.Create("初稿", "zh", "en", new[] { Entity(source) });
        var first = firstStore.Load(created.Id);
        var stale = new TranslationProjectStore(account).Load(created.Id);

        first.Name = "窗口一";
        firstStore.Save(first);
        stale.Name = "窗口二";
        var error = Assert.Throws<ProjectConflictException>(() => new TranslationProjectStore(account).Save(stale));

        Assert.Equal(created.Id, error.ProjectId);
        Assert.Equal(1, error.ExpectedRevision);
        Assert.Equal(2, error.ActualRevision);
        Assert.Equal("窗口一", firstStore.Load(created.Id).Name);
        Assert.Equal(1, stale.Revision);
    }

    [Fact]
    public void ConcurrentAppendExportAcrossStoreInstancesPreservesEveryRecord()
    {
        var account = Path.Combine(_root, "parallel-history");
        var source = Source("parallel-history");
        var store = new TranslationProjectStore(account);
        var project = store.Create("并发导出", "zh", "en", new[] { Entity(source) });

        Parallel.For(0, 12, index =>
        {
            new TranslationProjectStore(account).AppendExport(project.Id, new TranslationProjectExport
            {
                OutputDirectory = Path.Combine(_root, "out-" + index),
                WritebackMode = "offline"
            });
        });

        var loaded = store.Load(project.Id);
        Assert.Equal(12, loaded.ExportHistory.Count);
        Assert.Equal(13, loaded.Revision);
        Assert.Equal(12, loaded.ExportHistory.Select(item => item.OutputDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void LegacyProjectWithoutRevisionLoadsAsZeroAndUpgradesOnSave()
    {
        var account = Path.Combine(_root, "legacy-revision");
        var source = Source("legacy-revision");
        var store = new TranslationProjectStore(account);
        var project = store.Create("旧项目", "zh", "en", new[] { Entity(source) });
        var projectPath = Path.Combine(account, "projects", project.Id, "project.json");
        var json = JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
        json.Remove("Revision");
        File.WriteAllText(projectPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var legacy = store.Load(project.Id);
        Assert.Equal(0, legacy.Revision);
        legacy.Name = "已升级";
        store.Save(legacy);

        Assert.Equal(1, legacy.Revision);
        Assert.Equal("已升级", store.Load(project.Id).Name);
    }
}
