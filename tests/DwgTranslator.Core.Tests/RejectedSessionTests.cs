using System.Net;
using DwgTranslator.Core.Api;
namespace DwgTranslator.Core.Tests;

public sealed class RejectedSessionTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(send(request));
    }
    private static HttpResponseMessage Reply(int status, string body = "{}")
        => new((HttpStatusCode)status) { Content = new StringContent(body) };

    [Fact]
    public async Task RejectedDiskTokenIsNotSentAgainButNewSessionWorks()
    {
        var calls = 0;
        var token = "expired";
        using var http = new HttpClient(new Handler(r => { calls++; return Reply(r.Headers.Authorization?.Parameter == "expired" ? 401 : 200); }));
        var client = new WorkerApiClient(http, "https://unit.invalid", () => token, "device", "name");
        await Assert.ThrowsAsync<ApiAuthenticationException>(() => client.GetProfileAsync());
        await Assert.ThrowsAsync<ApiAuthenticationException>(() => client.GetProfileAsync());
        Assert.Null(await client.GetDevicesAsync());
        Assert.False(await client.RevokeDeviceAsync("other"));
        Assert.True(await client.LogoutAsync());
        Assert.Equal(1, calls);
        token = "fresh";
        await client.GetProfileAsync();
        Assert.Equal(2, calls);
        token = "expired";
        await Assert.ThrowsAsync<ApiAuthenticationException>(() => client.GetProfileAsync());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    public async Task AcknowledgedLogoutBlocksStillReadableToken(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(r => { calls++; return Reply(status); }));
        var client = new WorkerApiClient(http, "https://unit.invalid", () => "old", "device", "name");
        Assert.True(await client.LogoutAsync());
        await Assert.ThrowsAsync<ApiAuthenticationException>(() => client.GetProfileAsync());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task NonAuthenticationFailureDoesNotRejectToken(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(r => { calls++; return Reply(status); }));
        var client = new WorkerApiClient(http, "https://unit.invalid", () => "valid", "device", "name");
        await client.GetProfileAsync();
        await client.GetProfileAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationStillWinsWhenTokenWasRejected()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(r => { calls++; return Reply(401); }));
        var client = new WorkerApiClient(http, "https://unit.invalid", () => "old", "device", "name");
        await Assert.ThrowsAsync<ApiAuthenticationException>(() => client.GetProfileAsync());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetProfileAsync(canceled.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LoginAndBothUpdateSourcesRemainAnonymousAfterRejection()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(r => {
            var path = r.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/v1/profile") return Reply(401);
            Assert.Null(r.Headers.Authorization);
            if (path == "/v1/auth/login") return Reply(200, "{\"token\":\"fresh\"}");
            if (path == "/v1/version") return Reply(200, "{\"latest_version\":\"2.2.0\"}");
            return Reply(404);
        }));
        var client = new WorkerApiClient(http, "https://unit.invalid", () => "old", "device", "name");
        await Assert.ThrowsAsync<ApiAuthenticationException>(() => client.GetProfileAsync());
        Assert.True((await client.LoginAsync("dummy", "dummy")).Success);
        Assert.NotNull(await client.CheckVersionAsync("2.1.1"));
        Assert.Contains("/v1/version", paths);
        Assert.Contains("/update/latest.json", paths);
    }
}
