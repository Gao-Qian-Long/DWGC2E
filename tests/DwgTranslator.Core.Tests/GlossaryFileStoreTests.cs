using System.Text.Json;
using DwgTranslator.Core.Infrastructure.Glossary;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Tests;

public sealed class GlossaryFileStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-glossary-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "account", "glossaries", "mechanical_zh_en.json");

    [Fact]
    public void SaveCreatesDirectoriesAndKeepsTheExistingJsonContractAndMetadata()
    {
        var entries = new[] { new GlossaryEntry { Source = "轴", Target = "shaft", Category = "机械", Folder = "folder", CloudId = "fixture", CloudNote = "note", SourceKind = GlossarySource.User, Enabled = false, HitCount = 7 } };
        var expected = JsonSerializer.Serialize(entries, AppConfigJson.WriteOptions);
        GlossaryFileStore.Save(FilePath, entries);
        Assert.Equal(expected, File.ReadAllText(FilePath));
        Assert.Equal(expected, JsonSerializer.Serialize(entries, AppConfigJson.WriteOptions));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SaveReplacesPreviousContentsAndAllowsAnEmptyGlossary()
    {
        GlossaryFileStore.Save(FilePath, new[] { new GlossaryEntry { Source = "a", Target = "b" } });
        GlossaryFileStore.Save(FilePath, Array.Empty<GlossaryEntry>());
        Assert.Equal("[]", File.ReadAllText(FilePath));
        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void LockedDestinationPreservesOriginalAndCleansTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlossaryFileStore.Save(FilePath, new[] { new GlossaryEntry { Source = "a", Target = "b" } });
        var before = File.ReadAllBytes(FilePath);
        using (var locked = File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => GlossaryFileStore.Save(FilePath, Array.Empty<GlossaryEntry>()));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void InvalidDestinationDoesNotReplaceTheExistingDirectory()
    {
        Directory.CreateDirectory(FilePath);
        var error = Record.Exception(() => GlossaryFileStore.Save(FilePath, Array.Empty<GlossaryEntry>()));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(FilePath));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("dwgc2e-glossary-store-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture root");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}