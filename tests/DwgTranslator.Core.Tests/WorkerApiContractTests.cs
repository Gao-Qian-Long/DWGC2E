using System.Net;
using System.Text;
using System.Text.Json;
using DwgTranslator.Core.Api;

namespace DwgTranslator.Core.Tests;

public class WorkerApiContractTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static TranslationBatchRequest Batch() => new()
    {
        SourceLang = "ZH", TargetLang = "EN",
        Items = [new() { Id = 7, Text = "倒角", Height = 2.5, Rotation = 90, Layer = "NOTES" }]
    };
    private static WorkerApiClient Client(HttpClient http, Func<string?>? token = null) =>
        new(http, "https://worker.invalid", token ?? (() => "test-token"), "test-device", "test-host");

    [Fact]
    public async Task Translate_SendsSnakeCaseAndBearer_ParsesSnakeCaseResponse()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/translate", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.Equal("ZH", body.RootElement.GetProperty("source_lang").GetString());
            var item = body.RootElement.GetProperty("items")[0];
            Assert.Equal(7, item.GetProperty("id").GetInt32());
            Assert.Equal(90, item.GetProperty("rotation").GetDouble());
            Assert.Equal("NOTES", item.GetProperty("layer").GetString());
            Assert.True(body.RootElement.GetProperty("protection").GetProperty("protect_dimensions").GetBoolean());
            return Json("""{"success":true,"characters_used":2,"cached_count":1,"items":[{"id":7,"translated_text":"Chamfer","from_cache":true}]}""");
        }));
        var result = await Client(http).TranslateAsync(Batch());
        Assert.True(result.Success);
        Assert.Equal(2, result.CharactersUsed);
        Assert.Equal(1, result.CachedCount);
        Assert.Equal("Chamfer", Assert.Single(result.Items).TranslatedText);
        Assert.True(result.Items[0].FromCache);
    }

    [Fact]
    public async Task Translate_MissingIdIsExplicitFailure()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("""{"success":true,"items":[]}"""))));
        var result = await Client(http).TranslateAsync(Batch());
        Assert.Equal("missing_result", Assert.Single(result.Items).ErrorCode);
    }

    [Theory]
    [InlineData(401, "token_expired")]
    [InlineData(402, "quota_exceeded")]
    [InlineData(429, "rate_limited")]
    [InlineData(503, "upstream_unavailable")]
    public async Task Translate_MapsStatusWithoutJsonBody(int status, string error)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("", (HttpStatusCode)status))));
        var result = await Client(http).TranslateAsync(Batch());
        Assert.False(result.Success);
        Assert.Equal(error, result.ErrorCode);
    }

    [Fact]
    public async Task Translate_CallerCancellationPropagates()
    {
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }));
        using var cts = new CancellationTokenSource();
        var pending = Client(http).TranslateAsync(Batch(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task TokenProvider_IsReadForEveryRequest()
    {
        string token = "first";
        var seen = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            seen.Add(request.Headers.Authorization!.Parameter!);
            return Task.FromResult(Json("""{"display_name":"Engineer","email":"test@example.invalid","is_active":true}"""));
        }));
        var client = Client(http, () => token);
        Assert.Equal("Engineer", (await client.GetProfileAsync())!.DisplayName);
        token = "second";
        await client.GetProfileAsync();
        Assert.Equal(new[] { "first", "second" }, seen);
    }
}
