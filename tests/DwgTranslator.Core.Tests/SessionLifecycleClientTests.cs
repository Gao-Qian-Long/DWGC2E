using System.Net;
using DwgTranslator.Core.Api;
namespace DwgTranslator.Core.Tests;
public sealed class SessionLifecycleClientTests
{
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => send(request,ct);
    }
    private static WorkerApiClient Client(HttpClient http,Func<string?> token) => new(http,"https://unit.invalid",token,"installation","device");
    [Theory]
    [InlineData(200,true)]
    [InlineData(401,true)]
    [InlineData(403,false)]
    [InlineData(500,false)]
    public async Task LogoutOnlyAcknowledgesSuccessOrAlreadyInvalidSession(int status,bool expected)
    {
        using var http=new HttpClient(new Handler((request,ct)=>{
            Assert.Equal(HttpMethod.Post,request.Method);
            Assert.Equal("/v1/auth/logout",request.RequestUri!.AbsolutePath);
            Assert.Equal("old-session",request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent("{}")});
        }));
        Assert.Equal(expected,await Client(http,()=>"old-session").LogoutAsync());
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }
    [Fact]
    public async Task NetworkFailureIsNotReportedAsServerRevocation()
    {
        using var http=new HttpClient(new Handler((r,ct)=>throw new HttpRequestException("isolated offline")));
        Assert.False(await Client(http,()=>"old-session").LogoutAsync());
    }
    [Theory]
    [InlineData(200,"new-session")]
    [InlineData(401,"new-session")]
    [InlineData(200,"")]
    [InlineData(401,"")]
    public async Task DelayedProfileResponseCannotAffectChangedOrClearedSession(int status,string next)
    {
        var token="old-session";
        var started=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http=new HttpClient(new Handler((r,ct)=>{Assert.Equal("old-session",r.Headers.Authorization!.Parameter);started.SetResult(true);return response.Task;}));
        var pending=Client(http,()=>token).GetProfileAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        token=next;
        response.SetResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent("{\"user_id\":\"old-user\",\"error_code\":\"unauthenticated\"}")});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.Equal(next,token);
    }
    [Fact]
    public async Task DelayedLogoutCannotAcknowledgeNewSession()
    {
        var token="old-session";
        using var http=new HttpClient(new Handler((r,ct)=>{token="new-session";return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{}")});}));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Client(http,()=>token).LogoutAsync());
        Assert.Equal("new-session",token);
    }
}
