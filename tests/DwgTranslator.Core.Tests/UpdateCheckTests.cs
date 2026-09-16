using System.Net;
using System.Text;
using DwgTranslator.Core.Api;

namespace DwgTranslator.Core.Tests;

public sealed class UpdateCheckTests
{
    private const string Manifest = """{"latest_version":"2.1.2","download_url":"https://downloads.example.test/app.zip","release_notes":"fixture","mandatory":false}""";
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ExternalManifestDoesNotReceiveAccountTokenOrTriggerDownload()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((r, _) => {
            calls++;
            Assert.Equal("https://static.example.test/update/latest.json", r.RequestUri!.AbsoluteUri);
            Assert.Null(r.Headers.Authorization);
            return Task.FromResult(Json(Manifest));
        }));
        var client = new WorkerApiClient(http, "https://api.example.test", "https://static.example.test/update/latest.json", () => "fixture-token", "d", "h");
        var info = await client.CheckVersionAsync("2.1.1");
        Assert.Equal("2.1.2", info!.LatestVersion);
        Assert.Equal("https://downloads.example.test/app.zip", info.DownloadUrl);
        Assert.False(info.Mandatory);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(404, "{}")]
    [InlineData(200, "not-json")]
    public async Task UnavailableOrMalformedManifestFallsBackWithoutLosingCustomApiPrefix(int status, string body)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((r, _) => {
            if (++calls == 1) {
                Assert.Equal("/private-api/update/latest.json", r.RequestUri!.AbsolutePath);
                Assert.Null(r.Headers.Authorization);
                return Task.FromResult(Json(body, (HttpStatusCode)status));
            }
            Assert.Equal("/private-api/v1/version", r.RequestUri!.AbsolutePath);
            Assert.Equal("?current=2.1.1%2Blocal%20build", r.RequestUri.Query);
            Assert.Null(r.Headers.Authorization); // Version metadata is public, even after session rejection.
            return Task.FromResult(Json(Manifest));
        }));
        var client = new WorkerApiClient(http, "https://api.example.test/private-api", () => "fixture-token", "d", "h");
        Assert.Equal("2.1.2", (await client.CheckVersionAsync("2.1.1+local build"))!.LatestVersion);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("updates/local.json")]
    [InlineData("/updates/local.json")]
    public async Task RelativeManifestPreservesConfiguredApiPrefix(string manifest)
    {
        using var http = new HttpClient(new Handler((r, _) => {
            Assert.Equal("https://api.example.test/private-api/updates/local.json", r.RequestUri!.AbsoluteUri);
            Assert.Null(r.Headers.Authorization);
            return Task.FromResult(Json(Manifest));
        }));
        var client = new WorkerApiClient(http, "https://api.example.test/private-api/", manifest, () => "fixture-token", "d", "h");
        Assert.NotNull(await client.CheckVersionAsync("2.1.1"));
    }

    [Fact]
    public async Task MissingConfigurationDoesNotSendRequests()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        var client = new WorkerApiClient(http, "", () => "fixture-token", "d", "h");
        Assert.Null(await client.CheckVersionAsync("2.1.1"));
    }

    [Fact]
    public async Task CallerCancellationDoesNotFallBackToAnotherEndpoint()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, ct) => {
            calls++;
            cancellation.Cancel();
            await Task.Delay(10000, ct);
            return Json(Manifest);
        }));
        var client = new WorkerApiClient(http, "https://api.example.test", () => "fixture-token", "d", "h");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckVersionAsync("2.1.1", cancellation.Token));
        Assert.Equal(1, calls);
    }
}
