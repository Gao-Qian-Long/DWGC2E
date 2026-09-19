using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class TranslationProjectExportLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dwg-export-locator-" + Guid.NewGuid().ToString("N"));

    public TranslationProjectExportLocatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LatestExistingExportIsReturnedWhenNewestHistoryEntryWasMoved()
    {
        var source = Path.Combine(_root, "drawing.dwg");
        var oldOutput = Path.Combine(_root, "out", "drawing-old.dwg");
        var newestOutput = Path.Combine(_root, "out", "drawing-new.dwg");
        Directory.CreateDirectory(Path.GetDirectoryName(oldOutput)!);
        File.WriteAllText(source, "source");
        File.WriteAllText(oldOutput, "old");

        var project = new TranslationProject
        {
            ExportHistory = new List<TranslationProjectExport>
            {
                new()
                {
                    ExportedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                    Files = new List<TranslationProjectExportFile>
                    {
                        new() { SourcePath = source, OutputPath = oldOutput }
                    }
                },
                new()
                {
                    ExportedAtUtc = DateTime.UtcNow,
                    Files = new List<TranslationProjectExportFile>
                    {
                        new() { SourcePath = source, OutputPath = newestOutput }
                    }
                }
            }
        };

        Assert.Equal(Path.GetFullPath(oldOutput),
            TranslationProjectExportLocator.FindLatestOutputPath(project, source));
        Assert.Equal(Path.GetFullPath(newestOutput),
            TranslationProjectExportLocator.FindLatestOutputPath(project, source, requireExistingFile: false));
    }

    [Fact]
    public void SourceMatchingIsCaseInsensitiveAndUnrelatedDrawingsAreIgnored()
    {
        var source = Path.Combine(_root, "Drawing.dwg");
        var output = Path.Combine(_root, "Drawing-en.dwg");
        var unrelated = Path.Combine(_root, "Other.dwg");
        File.WriteAllText(output, "output");

        var project = new TranslationProject
        {
            ExportHistory = new List<TranslationProjectExport>
            {
                new()
                {
                    Files = new List<TranslationProjectExportFile>
                    {
                        new() { SourcePath = unrelated, OutputPath = Path.Combine(_root, "other-en.dwg") },
                        new() { SourcePath = source.ToLowerInvariant(), OutputPath = output }
                    }
                }
            }
        };

        Assert.Equal(Path.GetFullPath(output),
            TranslationProjectExportLocator.FindLatestOutputPath(project, source));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
