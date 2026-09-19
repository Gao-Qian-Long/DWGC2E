using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class ProductDataDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-product-data-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FirstMigrationCopiesRoamingAndPortableDataWithoutOverwriting()
    {
        var target = Dir("target");
        var roaming = Dir("roaming");
        var install = Dir("install");
        Put(target, "settings.json", "target-wins");
        Put(roaming, "settings.json", "roaming");
        Put(roaming, "accounts/account.json", "account");
        Put(install, "settings.json", "install");
        Put(install, "glossaries/portable.json", "glossary");
        Put(install, "not-user-data.txt", "do-not-copy");

        ProductDataDirectory.MigrateOnce(target, roaming, install);

        Assert.Equal("target-wins", Read(target, "settings.json"));
        Assert.Equal("account", Read(target, "accounts/account.json"));
        Assert.Equal("glossary", Read(target, "glossaries/portable.json"));
        Assert.False(File.Exists(Path.Combine(target, "not-user-data.txt")));
        Assert.Single(Directory.GetFiles(target, ".dwgc2e-localappdata-v1"));
    }

    [Fact]
    public void RoamingDataHasPriorityOverPortableData()
    {
        var target = Dir("target");
        var roaming = Dir("roaming");
        var install = Dir("install");
        Put(roaming, "projects/shared.json", "roaming-newer");
        Put(install, "projects/shared.json", "portable-older");

        ProductDataDirectory.MigrateOnce(target, roaming, install);

        Assert.Equal("roaming-newer", Read(target, "projects/shared.json"));
    }

    [Fact]
    public void MarkerMakesMigrationOneTimeOnly()
    {
        var target = Dir("target");
        var roaming = Dir("roaming");
        Put(roaming, "settings.json", "first");
        ProductDataDirectory.MigrateOnce(target, roaming, null);
        File.Delete(Path.Combine(target, "settings.json"));
        Put(roaming, "settings.json", "second");

        ProductDataDirectory.MigrateOnce(target, roaming, null);

        Assert.False(File.Exists(Path.Combine(target, "settings.json")));
    }

    [Fact]
    public async Task ConcurrentMigrationsSerializeAndProduceOneCompleteResult()
    {
        var target = Dir("target");
        var roaming = Dir("roaming");
        for (var i = 0; i < 100; i++) Put(roaming, $"projects/p{i:D3}.json", new string((char)('a' + i % 26), 256));

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            ProductDataDirectory.MigrateOnce(target, roaming, null))));

        Assert.Equal(100, Directory.GetFiles(Path.Combine(target, "projects"), "*.json").Length);
        Assert.Single(Directory.GetFiles(target, ".dwgc2e-localappdata-v1"));
        Assert.Empty(Directory.GetFiles(target, "*.tmp"));
    }

    [Fact]
    public void SourceReparsePointIsNeverFollowed()
    {
        var target = Dir("target");
        var roaming = Dir("roaming");
        var outside = Dir("outside");
        Put(outside, "secret.txt", "must-not-copy");
        var link = Path.Combine(roaming, "projects", "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }

        ProductDataDirectory.MigrateOnce(target, roaming, null);

        Assert.False(File.Exists(Path.Combine(target, "projects", "linked", "secret.txt")));
    }

    [Fact]
    public void DestinationReparsePointFailsClosedAndCannotEscape()
    {
        var target = Dir("target");
        var roaming = Dir("roaming");
        var outside = Dir("outside");
        Put(roaming, "projects/secret.txt", "must-not-escape");
        var link = Path.Combine(target, "projects");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }

        Assert.ThrowsAny<IOException>(() => ProductDataDirectory.MigrateOnce(target, roaming, null));
        Assert.False(File.Exists(Path.Combine(outside, "secret.txt")));
        Assert.False(File.Exists(Path.Combine(target, ".dwgc2e-localappdata-v1")));
    }

    [Fact]
    public void TargetInsideSourceIsRejectedBeforeTraversal()
    {
        var roaming = Dir("roaming");
        var target = Path.Combine(roaming, "nested-target");
        Directory.CreateDirectory(target);
        Put(roaming, "settings.json", "source");

        Assert.Throws<InvalidOperationException>(() => ProductDataDirectory.MigrateOnce(target, roaming, null));
        Assert.False(File.Exists(Path.Combine(target, ".dwgc2e-localappdata-v1")));
    }

    private string Dir(string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Put(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string directory, string relative) =>
        File.ReadAllText(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("dwgc2e-product-data-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture directory");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
