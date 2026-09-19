using ACadSharp;
using ACadSharp.IO;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// 导出失败不能留下备份：备份必须在"新图纸已经写好、且马上要提交"之后才落盘。
/// 旧实现在方法开头无条件备份，取消 / 没有一条能写 / 提交失败都会先留下源图副本，
/// 配合递增的 x.dwg.2.bak 命名，几次失败就在源图旁边堆起多份整图。
/// </summary>
public sealed class WriterBackupTimingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-backup-timing-" + Guid.NewGuid().ToString("N"));
    public WriterBackupTimingTests() => Directory.CreateDirectory(root);

    private (string Source, List<TextEntity> Entities) CreateDrawing(string extension)
    {
        var source = Path.Combine(root, "source" + extension);
        var doc = new CadDocument();
        doc.Entities.Add(new ACadSharp.Entities.TextEntity { Value = "Motor", Height = 3 });
        if (extension == ".dxf") DxfWriter.Write(source, doc, false);
        else DwgWriter.Write(source, doc);
        var entities = new DwgReaderService().ExtractFromFile(source);
        Assert.Single(entities);
        entities[0].TranslatedText = "Engine";
        entities[0].Status = TranslationStatus.Translated;
        return (source, entities);
    }

    private WritebackOptions BackupOptions(string fileName, bool overwriteExisting = false) => new()
    {
        BackupSource = true,
        BackupPath = Path.Combine(root, fileName),
        OverwriteExisting = overwriteExisting
    };

    [Theory]
    [InlineData(".dxf")]
    [InlineData(".dwg")]
    public void FailingExportLeavesNoBackupAndNoPartialOutput(string extension)
    {
        var (source, entities) = CreateDrawing(extension);
        var original = File.ReadAllBytes(source);
        var backup = Path.Combine(root, "source" + extension + ".bak");
        var output = Path.Combine(root, "out" + extension);
        var writer = new DwgWriterService();
        var options = BackupOptions("source" + extension + ".bak");

        // 没有可写回的条目：早退发生在备份之前，不得产生任何备份。
        var nothingToWrite = writer.WriteTranslations(source, output, [], false, options: options);
        Assert.Equal(0, nothingToWrite.SuccessCount);
        Assert.False(File.Exists(backup));

        // 写回前就被取消：同上，取消的导出不得留下备份。
        Assert.Throws<OperationCanceledException>(() => writer.WriteTranslations(
            source, output, entities, false, new CancellationToken(canceled: true), options));
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(output));

        // 提交阶段失败（没有显式覆盖授权，目标已存在）：这一次备份已经产生（新图纸已经写好），
        // 但只有这一份，源图本身一个字节都没变。
        File.WriteAllText(output, "existing output");
        var commitFailure = writer.WriteTranslations(source, output, entities, false, options: options);

        Assert.Equal(0, commitFailure.SuccessCount);
        Assert.NotEmpty(commitFailure.Errors);
        Assert.Equal("existing output", File.ReadAllText(output));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Single(Directory.GetFiles(root, "*.bak"));
        Assert.False(File.Exists(source + ".2.bak"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData(".dxf")]
    [InlineData(".dwg")]
    public void RepeatedExportsReuseTheSingleBackupFile(string extension)
    {
        var (source, entities) = CreateDrawing(extension);
        var output = Path.Combine(root, "out" + extension);
        var backup = Path.Combine(root, "source" + extension + ".bak");
        var writer = new DwgWriterService();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = writer.WriteTranslations(source, output, entities, false,
                options: BackupOptions("source" + extension + ".bak", overwriteExisting: true));
            Assert.True(result.Errors.Count == 0, "attempt " + attempt + " errors: " + string.Join(" | ", result.Errors)
                + " files=" + string.Join(",", Directory.GetFiles(root).Select(Path.GetFileName)));
            Assert.Equal(1, result.SuccessCount);
        }

        Assert.True(File.Exists(backup));
        // 只可能有一个回滚点：重试不会产出 .2.bak / .3.bak
        Assert.Single(Directory.GetFiles(root, "*.bak"));
        Assert.Equal("source" + extension + ".bak", Path.GetFileName(backup));
        Assert.False(File.Exists(source + "." + extension + ".2.bak"));
    }

    public void Dispose() => Directory.Delete(root, true);
}
