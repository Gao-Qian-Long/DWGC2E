using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
namespace DwgTranslator.Core.Tests;
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dwgc2e-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "settings.json");
    public SettingsStoreTests() => Directory.CreateDirectory(directory);
    [Fact] public void MigrationBacksUpAndPreservesOrdinarySettings()
    {
        SettingsStore.Update(FilePath, c => { c.ApiMode = "direct"; c.ApiBaseUrl = ""; c.ExportDirectory = "custom-output"; c.AuthTokenEncrypted = "opaque-session"; });
        var original = File.ReadAllText(FilePath);
        SettingsStore.Migrate(FilePath);
        var result = SettingsStore.Read(FilePath);
        Assert.Equal("worker", result.ApiMode);
        Assert.Equal(new AppConfig().ApiBaseUrl, result.ApiBaseUrl);
        Assert.Equal("custom-output", result.ExportDirectory);
        Assert.Equal("opaque-session", result.AuthTokenEncrypted);
        Assert.Equal(original, File.ReadAllText(FilePath + ".pre-ui-v1.bak"));
        var migrated = File.ReadAllText(FilePath);
        SettingsStore.Migrate(FilePath);
        Assert.Equal(migrated, File.ReadAllText(FilePath));
    }
    [Fact] public void EmptyAddressUsesDeploymentDefaults()
    {
        SettingsStore.Update(FilePath, c => c.ApiBaseUrl = "");
        SettingsStore.Migrate(FilePath, new AppConfig { ApiBaseUrl = "https://deployment.example.test" });
        Assert.Equal("https://deployment.example.test", SettingsStore.Read(FilePath).ApiBaseUrl);
    }
    [Theory] [InlineData("https://custom.example.test")] [InlineData("not a valid URI")]
    public void ExplicitAddressIsNeverSilentlyReplaced(string address)
    {
        SettingsStore.Update(FilePath, c => c.ApiBaseUrl = address);
        SettingsStore.Migrate(FilePath);
        Assert.Equal(address, SettingsStore.Read(FilePath).ApiBaseUrl);
    }
    [Fact] public void ConcurrentOwnedFieldUpdatesDoNotLoseSession()
    {
        Parallel.Invoke(() => SettingsStore.Update(FilePath, c => c.AuthTokenEncrypted = "new-session"),
            () => SettingsStore.Update(FilePath, c => c.ExportDirectory = "new-output"));
        var result = SettingsStore.Read(FilePath);
        Assert.Equal("new-session", result.AuthTokenEncrypted);
        Assert.Equal("new-output", result.ExportDirectory);
    }
    [Fact] public void FailedEditLeavesOriginalUntouched()
    {
        SettingsStore.Update(FilePath, c => c.ExportDirectory = "original");
        var original = File.ReadAllText(FilePath);
        Assert.Throws<IOException>(() => SettingsStore.Update(FilePath, c => { c.ExportDirectory = "lost"; throw new IOException(); }));
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact] public void LockedFileDoesNotLeavePartialConfiguration()
    {
        if (!OperatingSystem.IsWindows()) return;
        SettingsStore.Update(FilePath, c => c.ExportDirectory = "original");
        var before = File.ReadAllText(FilePath);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => SettingsStore.Update(FilePath, c => c.ExportDirectory = "not-saved"));
        Assert.Equal(before, File.ReadAllText(FilePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact] public void CorruptFileIsNotReplacedWithDefaults()
    {
        File.WriteAllText(FilePath, "{broken");
        Assert.Throws<System.Text.Json.JsonException>(() => SettingsStore.Migrate(FilePath));
        Assert.Equal("{broken", File.ReadAllText(FilePath));
    }
    [Fact] public void GlossaryClonePreservesMetadata()
    {
        var source = new GlossaryEntry { Source="a", Target="b", Folder="folder", Category="category", Direction="ZH-EN", SourceKind=GlossarySource.User, HitCount=8, LastHitAt=DateTime.UtcNow, Enabled=false };
        var clone = source.Clone();
        Assert.NotSame(source, clone);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(source), System.Text.Json.JsonSerializer.Serialize(clone));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
