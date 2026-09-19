using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
namespace DwgTranslator.Core.Tests;
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dwgc2e-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "settings.json");
    public SettingsStoreTests() => Directory.CreateDirectory(directory);
    [Fact] public void RejectedCredentialCleanupPreservesOtherFields()
    {
        SettingsStore.Update(FilePath, c => { c.AuthTokenEncrypted = "old"; c.ActiveAccountId = "owner"; c.ExportDirectory = "custom"; });
        Assert.True(SettingsStore.ClearAuthenticationIfMatches(FilePath, "old"));
        var current = SettingsStore.Read(FilePath);
        Assert.Equal("", current.AuthTokenEncrypted);
        Assert.Equal("owner", current.ActiveAccountId);
        Assert.Equal("custom", current.ExportDirectory);
    }
    [Fact] public void RejectedCredentialCleanupDoesNotRewriteNewSession()
    {
        SettingsStore.Update(FilePath, c => c.AuthTokenEncrypted = "new");
        var bytes = File.ReadAllBytes(FilePath);
        Assert.False(SettingsStore.ClearAuthenticationIfMatches(FilePath, "old"));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.False(SettingsStore.ClearAuthenticationIfMatches(FilePath + ".missing", "old"));
        Assert.False(File.Exists(FilePath + ".missing"));
    }
    [Fact] public void RejectedCredentialCleanupCanRetryAfterFileUnlock()
    {
        if (!OperatingSystem.IsWindows()) return;
        SettingsStore.Update(FilePath, c => c.AuthTokenEncrypted = "old");
        var bytes = File.ReadAllBytes(FilePath);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => SettingsStore.ClearAuthenticationIfMatches(FilePath, "old"));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        }
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.True(SettingsStore.ClearAuthenticationIfMatches(FilePath, "old"));
    }
    [Fact] public void MigrationBacksUpAndPreservesOrdinarySettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{\"configurationVersion\":0,\"apiMode\":\"direct\",\"apiBaseUrl\":\"\",\"exportDirectory\":\"custom-output\",\"authTokenEncrypted\":\"opaque-session\"}");
        var original = File.ReadAllText(FilePath);
        SettingsStore.Migrate(FilePath);
        var result = SettingsStore.Read(FilePath);
        using var migratedDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.DoesNotContain(migratedDocument.RootElement.EnumerateObject(), property => property.Name.Equals("apiMode", StringComparison.OrdinalIgnoreCase));
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
    [Fact]
    public void NullDocumentFailsInsteadOfRewritingDefaultsOverUserSettings()
    {
        // 字面量 null 是合法 JSON：旧实现反序列化成 null 后静默用默认值继续，紧接着 Update
        // 把默认值整份写回——令牌、账号 ID、输出目录全部被清空，用户毫不知情。
        File.WriteAllText(FilePath, "null");
        var original = File.ReadAllBytes(FilePath);

        Assert.Throws<System.IO.InvalidDataException>(() => SettingsStore.Read(FilePath));
        Assert.Throws<System.IO.InvalidDataException>(() => SettingsStore.Update(FilePath, c => c.AuthTokenEncrypted = "new"));
        Assert.Throws<System.IO.InvalidDataException>(() => SettingsStore.Migrate(FilePath));

        Assert.Equal(original, File.ReadAllBytes(FilePath)); // 磁盘文件一个字节都没变
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("\"text\"")]
    public void NonObjectDocumentIsNotTreatedAsAnEmptyConfiguration(string document)
    {
        File.WriteAllText(FilePath, document);
        var original = File.ReadAllBytes(FilePath);

        // 类型不匹配与"解析成 null"是不同的失败路径，但都必须明确失败且不动磁盘上的文件。
        var failure = Record.Exception(() => SettingsStore.Read(FilePath));
        Assert.True(failure is System.Text.Json.JsonException or System.IO.InvalidDataException,
            "unexpected: " + (failure?.GetType().FullName ?? "no exception"));

        Assert.Equal(original, File.ReadAllBytes(FilePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact]
    public void MissingFileIsStillAFreshInstallAndUsesDefaults()
    {
        Assert.NotNull(SettingsStore.Read(Path.Combine(directory, "not-there.json")));
        SettingsStore.Update(FilePath, c => c.ExportDirectory = "custom");
        Assert.Equal("custom", SettingsStore.Read(FilePath).ExportDirectory);
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
