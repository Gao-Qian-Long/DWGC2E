using System.Text.Json;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Tests;

public class ApiClientFactoryTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sourceLanguage\":\"ZH\",\"deepSeekApiKey\":\"legacy-key\"}")]
    public void LegacyProviderFieldsDoNotChangeWorkerSelection(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.ReadOptions)!;
        using var http = new HttpClient();
        var client = ApiClientFactory.Create(config, http, () => null, "test-device", "test-host");
        Assert.Equal("worker", client.ModeName);
        Assert.True(client.IsConfigured);
        Assert.Equal("https://api.cad.pocketter.dpdns.org", config.ApiBaseUrl);
    }

    [Fact]
    public void InvalidWorkerAddressIsNotConfiguredAndDoesNotFallBackToDirect()
    {
        using var http = new HttpClient();
        var client = ApiClientFactory.Create(new AppConfig { ApiBaseUrl = "" },
            http, () => null, "test-device", "test-host");
        Assert.Equal("worker", client.ModeName);
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void LegacyDirectModeJsonIsForcedBackToWorker()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{\"apiMode\":\" DIRECT \"}", AppConfigJson.ReadOptions)!;
        using var http = new HttpClient();
        var client = ApiClientFactory.Create(config, http, () => null, "test-device", "test-host");
        Assert.IsType<WorkerApiClient>(client);
        Assert.Equal("worker", client.ModeName);
    }

    [Fact]
    public void ShippedExampleIsValidTargetsWorkerAndOmitsProviderConfiguration()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DwgTranslator.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var example = File.ReadAllText(Path.Combine(root!.FullName, "settings.json.example"));
        using var document = JsonDocument.Parse(example);
        var config = JsonSerializer.Deserialize<AppConfig>(example, AppConfigJson.ReadOptions)!;
        Assert.Equal(new AppConfig().ApiBaseUrl, config.ApiBaseUrl);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiMode", names);
        Assert.DoesNotContain("deepSeekApiKey", names);
        Assert.DoesNotContain("deepSeekBaseUrl", names);
        Assert.DoesNotContain("deepSeekModel", names);
    }

#if !DEBUG
    [Fact]
    public void ProductionCoreOmitsDirectProviderPromptAndLegacyConfigurationSurface()
    {
        var assembly = typeof(AppConfig).Assembly;
        foreach (var typeName in new[]
        {
            "DwgTranslator.Core.Api.DirectApiClient",
            "DwgTranslator.Core.Application.Translation.SettingsBackedDeepSeekClient",
            "DwgTranslator.Core.Application.Translation.TranslationPrompt",
            "DwgTranslator.Core.Infrastructure.Api.DeepSeekClient",
            "DwgTranslator.Core.Infrastructure.Api.DeepSeekClientFactory"
        })
            Assert.Null(assembly.GetType(typeName, throwOnError: false));

        foreach (var propertyName in new[] { "ApiMode", "DeepSeekApiKey", "DeepSeekBaseUrl", "DeepSeekModel" })
            Assert.Null(typeof(AppConfig).GetProperty(propertyName));
    }
#endif
}