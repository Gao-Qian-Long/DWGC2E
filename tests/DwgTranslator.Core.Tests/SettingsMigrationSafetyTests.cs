using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Text.Json;

namespace DwgTranslator.Core.Tests;

/// <summary>Startup migration failure boundaries; fixtures never use real user configuration.</summary>
public sealed class SettingsMigrationSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-migration-safety-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "user", "settings.json");

    [Fact]
    public void CopiedFirstLaunchDefaultsMigrateWithoutChangingBundledConfiguration()
    {
        var defaults = new AppConfig { ApiBaseUrl = "https://custom.example.test/api", ApiMode = "direct", ConfigurationVersion = 0 };
        var before = JsonSerializer.Serialize(defaults);
        var bundle = Path.Combine(root, "bundle", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(bundle, before);
        File.Copy(bundle, Settings); // App.OnStartup copies bundled defaults before migrating.
        SettingsStore.Migrate(Settings, defaults);
        var saved = SettingsStore.Read(Settings);
        Assert.Equal(defaults.ApiBaseUrl, saved.ApiBaseUrl);
        Assert.Equal("worker", saved.ApiMode);
        Assert.Equal(1, saved.ConfigurationVersion);
        Assert.Equal(before, JsonSerializer.Serialize(defaults));
        Assert.Equal(before, File.ReadAllText(bundle));
        Assert.Equal(before, File.ReadAllText(Settings + ".pre-ui-v1.bak"));
        Assert.Equal(3, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void ExistingRecoveryBackupIsNeverReplacedByAnotherMigration()
    {
        SettingsStore.Update(Settings, c => { c.ConfigurationVersion = 0; c.ExportDirectory = "chosen-output"; });
        var backup = Settings + ".pre-ui-v1.bak";
        File.WriteAllText(backup, "earlier recovery data, not JSON");
        var before = File.ReadAllBytes(backup);
        SettingsStore.Migrate(Settings);
        Assert.Equal(before, File.ReadAllBytes(backup));
        Assert.Equal("chosen-output", SettingsStore.Read(Settings).ExportDirectory);
        Assert.Equal(1, SettingsStore.Read(Settings).ConfigurationVersion);
    }

    [Fact]
    public void BackupCreationFailureLeavesOriginalSchemaAndBytesIntact()
    {
        SettingsStore.Update(Settings, c => c.ConfigurationVersion = 0);
        var before = File.ReadAllBytes(Settings);
        Directory.CreateDirectory(Settings + ".pre-ui-v1.bak");
        Assert.ThrowsAny<IOException>(() => SettingsStore.Migrate(Settings));
        Assert.Equal(before, File.ReadAllBytes(Settings));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ReadableButReplacementLockedFilePreservesOriginalAndRemovesTemporaryFile()
    {
        // This differs from a FileShare.None test: reading and the edit succeed, the commit fails.
        if (!OperatingSystem.IsWindows()) return;
        SettingsStore.Update(Settings, c => c.ExportDirectory = "original");
        var before = File.ReadAllBytes(Settings);
        var editReached = false;
        using (var locked = new FileStream(Settings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => SettingsStore.Update(Settings, c =>
            {
                editReached = true;
                c.ExportDirectory = "must-not-commit";
            }));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected replacement failure");
        }
        Assert.True(editReached);
        Assert.Equal(before, File.ReadAllBytes(Settings));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void CorruptConfigurationDoesNotRunEditOrCreateBackup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, "{broken user configuration");
        var before = File.ReadAllBytes(Settings);
        var editReached = false;
        Assert.Throws<JsonException>(() => SettingsStore.Update(Settings, _ => editReached = true));
        Assert.Throws<JsonException>(() => SettingsStore.Migrate(Settings));
        Assert.False(editReached);
        Assert.Equal(before, File.ReadAllBytes(Settings));
        Assert.Equal(new[] { Settings }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FutureSchemaWithCustomEndpointIsNotDowngradedOrRewritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, "{\"ConfigurationVersion\":99,\"ApiBaseUrl\":\"https://custom.example.test/v99\",\"FutureField\":{\"preserve\":true}}");
        var before = File.ReadAllBytes(Settings);
        SettingsStore.Migrate(Settings);
        Assert.Equal(before, File.ReadAllBytes(Settings));
        Assert.Equal(new[] { Settings }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    public void EndpointMigrationPreservesUnknownFieldsAndFutureSchema(int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        var input = "{\"configurationVersion\":" + version +
            ",\"apiBaseUrl\":\"https://dwgc2e-api.maplehousezz.workers.dev\",\"FutureField\":{\"enabled\":true,\"items\":[1,null,\"text\"]},\"futureNull\":null,\"futureNumber\":1234567890123456789}";
        File.WriteAllText(Settings, input);
        SettingsStore.Migrate(Settings);
        using var saved = JsonDocument.Parse(File.ReadAllText(Settings));
        Assert.Equal(Math.Max(1, version), saved.RootElement.GetProperty("configurationVersion").GetInt32());
        Assert.Equal(DwgTranslator.Core.Api.ProductApiEndpoint.Default, saved.RootElement.GetProperty("apiBaseUrl").GetString());
        AssertFutureFields(saved.RootElement);
        Assert.False(saved.RootElement.TryGetProperty("additionalSettings", out _));
        if (version == 0) Assert.Equal(input, File.ReadAllText(Settings + ".pre-ui-v1.bak"));
        var migrated = File.ReadAllBytes(Settings);
        SettingsStore.Migrate(Settings);
        Assert.Equal(migrated, File.ReadAllBytes(Settings));
    }

    [Fact]
    public void OrdinaryEditAndCredentialClearPreserveUnknownFields()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, "{\"authTokenEncrypted\":\"rejected-fixture-token\",\"FutureField\":{\"enabled\":true,\"items\":[1,null,\"text\"]},\"futureNull\":null,\"futureNumber\":1234567890123456789}");
        SettingsStore.Update(Settings, c => c.ExportDirectory = "chosen-output");
        Assert.True(SettingsStore.ClearAuthenticationIfMatches(Settings, "rejected-fixture-token"));
        using var saved = JsonDocument.Parse(File.ReadAllText(Settings));
        AssertFutureFields(saved.RootElement);
        Assert.Equal("chosen-output", saved.RootElement.GetProperty("exportDirectory").GetString());
        Assert.Equal("", saved.RootElement.GetProperty("authTokenEncrypted").GetString());
    }

    private static void AssertFutureFields(JsonElement root)
    {
        var future = root.GetProperty("FutureField");
        Assert.True(future.GetProperty("enabled").GetBoolean());
        var items = future.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(1, items[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, items[1].ValueKind);
        Assert.Equal("text", items[2].GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("futureNull").ValueKind);
        Assert.Equal(1234567890123456789L, root.GetProperty("futureNumber").GetInt64());
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("dwgc2e-migration-safety-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture directory");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}