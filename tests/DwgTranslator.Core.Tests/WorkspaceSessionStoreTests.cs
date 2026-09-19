using System;
using System.IO;
using System.Text.Json;
using DwgTranslator.Core.Services;
namespace DwgTranslator.Core.Tests;

/// <summary>
/// "上次工作区"记录是便利缓存：读得出来就恢复，读不出来只退化成空工作区，
/// 任何情况下都不允许把启动搞崩（这几个用例就是那条边界）。
/// </summary>
public class WorkspaceSessionStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "workspace-session-" + Guid.NewGuid().ToString("N"), "workspace-session.json");

    [Fact]
    public void RoundTripsLanguagePairAndDrawings()
    {
        var path = TempFile();
        try
        {
            var store = new WorkspaceSessionStore(path);
            Assert.Null(store.Read()); // 没有记录 = 空工作区，不是错误

            store.Save(new WorkspaceSessionStore.Snapshot
            {
                SourceLanguage = "ZH",
                TargetLanguage = "JA",
                Drawings = new() { @"C:\drawings\a.dwg", @"C:\drawings\b.dxf" }
            });

            var snapshot = store.Read();
            Assert.NotNull(snapshot);
            Assert.Equal("ZH", snapshot!.SourceLanguage);
            Assert.Equal("JA", snapshot.TargetLanguage);
            Assert.Equal(new[] { @"C:\drawings\a.dwg", @"C:\drawings\b.dxf" }, snapshot.Drawings);
            Assert.Equal(WorkspaceSessionStore.SchemaVersion, snapshot.Version);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CorruptOrUnreadableFileDegradesToNoSession()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");
            Assert.Null(new WorkspaceSessionStore(path).Read());

            // 空白文件同样只是"没有记录"。
            File.WriteAllText(path, "   ");
            Assert.Null(new WorkspaceSessionStore(path).Read());

            // 字面量 null（老 bug 里 settings.json 就是这么被写坏的）也不能抛。
            File.WriteAllText(path, "null");
            Assert.Null(new WorkspaceSessionStore(path).Read());

            // 读不出来之后仍然能正常保存：损坏的记录会被这次保存覆盖，而不是把用户永久卡在空工作区。
            var store = new WorkspaceSessionStore(path);
            store.Save(new WorkspaceSessionStore.Snapshot { Drawings = new() { @"C:\drawings\c.dwg" } });
            Assert.Equal(new[] { @"C:\drawings\c.dwg" }, store.Read()!.Drawings);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RecordFromANewerSchemaIsIgnored()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = WorkspaceSessionStore.SchemaVersion + 1, Drawings = new[] { @"C:\drawings\a.dwg" } }));
            Assert.Null(new WorkspaceSessionStore(path).Read());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DrawingsAreDeduplicatedTruncatedAndBlanksDropped()
    {
        var path = TempFile();
        try
        {
            var store = new WorkspaceSessionStore(path);
            var drawings = new System.Collections.Generic.List<string> { @"C:\a.dwg", "   ", @"C:\A.DWG" };
            for (var index = 0; index < WorkspaceSessionStore.MaxDrawings + 5; index++) drawings.Add($@"C:\drawings\{index}.dwg");
            store.Save(new WorkspaceSessionStore.Snapshot { Drawings = drawings });

            var restored = store.Read()!.Drawings;
            // 大小写不同的同一张图纸只算一条；空路径不算；总数封顶，避免这份文件无限增长。
            Assert.Equal(WorkspaceSessionStore.MaxDrawings, restored.Count);
            Assert.Equal(1, restored.Count(item => item.Equals(@"C:\a.dwg", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(@"C:\a.dwg", restored[0]);
            Assert.Equal(@"C:\drawings\0.dwg", restored[1]);
            Assert.DoesNotContain(restored, item => string.IsNullOrWhiteSpace(item));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ClearRemovesTheRecordWithoutThrowing()
    {
        var path = TempFile();
        try
        {
            var store = new WorkspaceSessionStore(path);
            store.Save(new WorkspaceSessionStore.Snapshot { Drawings = new() { @"C:\a.dwg" } });
            Assert.NotNull(store.Read());
            store.Clear();
            Assert.Null(store.Read());
            store.Clear(); // 再删一次（文件已经不在）不能抛
        }
        finally { Cleanup(path); }
    }

    private static void Cleanup(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
