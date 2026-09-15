using System.Net;
using System.Net.Http;
using DwgTranslator.Core.Api;
namespace DwgTranslator.Core.Tests;
public sealed class WorkerSessionTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r);
    }
    [Fact] public async Task MissingIdentityDoesNotSendLoginRequest()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("No request expected")));
        var api = new WorkerApiClient(http, "https://worker.invalid", () => null, "", "PC");
        var result = await api.LoginAsync("alice", "password");
        Assert.False(result.Success); Assert.Equal("device_identity_unavailable", result.ErrorCode);
    }
    [Fact] public async Task LoginDoesNotForwardPreviousAccountToken()
    {
        using var http = new HttpClient(new Handler(r => { Assert.Null(r.Headers.Authorization); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true,\"token\":\"new\"}") }); }));
        var api = new WorkerApiClient(http,"https://worker.invalid",()=>"old","device","PC");
        Assert.True((await api.LoginAsync("alice","password")).Success);
    }
    [Fact] public async Task LogoutUsesCurrentSessionAndAcceptsAlreadyExpired()
    {
        using var http = new HttpClient(new Handler(r => { Assert.Equal("/v1/auth/logout",r.RequestUri!.AbsolutePath); Assert.Equal("Bearer old",r.Headers.Authorization!.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }); }));
        var api = new WorkerApiClient(http,"https://worker.invalid",()=>"old","device","PC");
        Assert.True(await api.LogoutAsync());
    }
    [Fact] public async Task LateResponseAfterSessionChangeIsDiscarded()
    {
        string token = "old";
        using var http = new HttpClient(new Handler(r => { token = "new"; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"account\":\"old-user\"}") }); }));
        var api = new WorkerApiClient(http,"https://worker.invalid",()=>token,"device","PC");
        await Assert.ThrowsAsync<OperationCanceledException>(()=>api.GetProfileAsync());
    }
}
