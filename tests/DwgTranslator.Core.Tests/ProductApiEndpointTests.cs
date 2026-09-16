using System.Net;
using System.Text;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class ProductApiEndpointTests
{
    [Theory]
    [InlineData("https://dwgc2e-api.maplehousezz.workers.dev")]
    [InlineData(" HTTPS://DWGC2E-API.MAPLEHOUSEZZ.WORKERS.DEV/ ")]
    public void OnlyFormerProductDefaultMigrates(string address) => Assert.Equal(ProductApiEndpoint.Default, ProductApiEndpoint.Migrate(address));

    [Theory]
    [InlineData("https://api.cad.pocketter.dpdns.org")]
    [InlineData("https://custom.example.test")]
    [InlineData("https://custom.example.test/api")]
    [InlineData("https://other.workers.dev")]
    [InlineData("https://dwgc2e-api.maplehousezz.workers.dev/custom")]
    [InlineData("https://dwgc2e-api.maplehousezz.workers.dev.evil.test")]
    [InlineData("https://dwgc2e-api.maplehousezz.workers.dev?custom=1")]
    [InlineData("")]
    [InlineData("not a valid URI")]
    public void ExplicitEndpointsAreNotRewritten(string address) => Assert.Equal(address, ProductApiEndpoint.Migrate(address));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PersistedMigrationPreservesDataAndIsIdempotent(int version)
    {
        var folder=Path.Combine(Path.GetTempPath(),"dwgc2e-domain-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(folder,"settings.json");
        try
        {
            SettingsStore.Update(path,c=>{c.ConfigurationVersion=version;c.ApiBaseUrl="https://dwgc2e-api.maplehousezz.workers.dev";c.AuthTokenEncrypted="opaque-token";c.ActiveAccountId="account-a";c.ExportDirectory="custom-output";});
            SettingsStore.Migrate(path);
            var result=SettingsStore.Read(path);
            Assert.Equal(ProductApiEndpoint.Default,result.ApiBaseUrl);
            Assert.Equal("opaque-token",result.AuthTokenEncrypted);Assert.Equal("account-a",result.ActiveAccountId);Assert.Equal("custom-output",result.ExportDirectory);
            var original=File.ReadAllText(path);SettingsStore.Migrate(path);Assert.Equal(original,File.ReadAllText(path));
            if(version==1) Assert.Single(Directory.GetFiles(folder)); // no new endpoint snapshots/backups
        }
        finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CustomEndpointSurvivesPersistenceMigrationAndClientConstruction(int version)
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-custom-endpoint-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        const string endpoint = "https://custom.example.test/private-api";
        try
        {
            SettingsStore.Update(path, c => {
                c.ConfigurationVersion = version;
                c.ApiBaseUrl = endpoint;
                c.AuthTokenEncrypted = "opaque-test-token";
                c.ActiveAccountId = "custom-account";
            });
            var original = File.ReadAllBytes(path);
            SettingsStore.Migrate(path);
            var config = SettingsStore.Read(path);
            Assert.Equal(endpoint, config.ApiBaseUrl);
            Assert.Equal("opaque-test-token", config.AuthTokenEncrypted);
            Assert.Equal("custom-account", config.ActiveAccountId);
            if (version == 0)
                Assert.Equal(original, File.ReadAllBytes(path + ".pre-ui-v1.bak"));
            else
                Assert.Equal(original, File.ReadAllBytes(path));
            var handler = new Handler();
            using var http = new HttpClient(handler);
            var client = ApiClientFactory.Create(config, http, () => "isolated-token", "d", "h");
            await ((ICloudGlossaryClient)client).ReadCloudGlossaryAsync();
            Assert.Equal(endpoint + "/v1/glossary", handler.LastUri!.AbsoluteUri);
            var migrated = File.ReadAllBytes(path);
            SettingsStore.Migrate(path);
            Assert.Equal(migrated, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Uri? LastUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            LastUri=request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"success\":true,\"revision\":\""+new string('a',64)+"\",\"entries\":[]}",Encoding.UTF8,"application/json")});
        }
    }
    [Fact] public async Task FactoryUsesCustomDomainEvenWhenPersistingMigrationWasUnavailable()
    {
        var handler=new Handler();using var http=new HttpClient(handler);
        var config=new AppConfig{ApiBaseUrl="https://dwgc2e-api.maplehousezz.workers.dev"};
        var client=ApiClientFactory.Create(config,http,()=>"isolated-token","d","h");
        await ((ICloudGlossaryClient)client).ReadCloudGlossaryAsync();
        Assert.Equal("https://api.cad.pocketter.dpdns.org/v1/glossary",handler.LastUri!.AbsoluteUri);
    }
}
