using System.Text.Json;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Tests;

public class ApiClientFactoryTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sourceLanguage\":\"ZH\",\"deepSeekApiKey\":\"legacy-key\"}")]
    public void MissingModeDefaultsToConfiguredWorker(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.ReadOptions)!;
        using var http = new HttpClient();
        var client = ApiClientFactory.Create(config, http, () => null, "test-device", "test-host",
            () => throw new Exception("Direct must not be constructed"));
        Assert.Equal("worker", client.ModeName);
        Assert.True(client.IsConfigured);
        Assert.Equal("https://dwgc2e-api.maplehousezz.workers.dev", config.ApiBaseUrl);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("wroker")]
    [InlineData(null)]
    public void InvalidWorkerAddressDoesNotFallBackToDirect(string? mode)
    {
        using var http = new HttpClient();
        var client = ApiClientFactory.Create(new AppConfig { ApiMode = mode!, ApiBaseUrl = "" },
            http, () => null, "test-device", "test-host",
            () => throw new Exception("Direct must not be constructed"));
        Assert.Equal("worker", client.ModeName);
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void LegacyDirectModeIsForcedBackToWorker()
    {
        using var http = new HttpClient();
        var called = false;
        var client = ApiClientFactory.Create(new AppConfig { ApiMode = " DIRECT " }, http, () => null, "test-device", "test-host",
            () => { called = true; throw new NotSupportedException(); });
        Assert.False(called);
        Assert.IsType<WorkerApiClient>(client);
    }

    [Fact]
    public void ShippedExampleIsValidAndTargetsWorker()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DwgTranslator.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var example = File.ReadAllText(Path.Combine(root!.FullName, "settings.json.example"));
        using var document = JsonDocument.Parse(example);
        var config = JsonSerializer.Deserialize<AppConfig>(example, AppConfigJson.ReadOptions)!;
        Assert.Equal("worker", config.ApiMode);
        Assert.Equal(new AppConfig().ApiBaseUrl, config.ApiBaseUrl);
        Assert.Empty(config.DeepSeekApiKey);
    }
}


