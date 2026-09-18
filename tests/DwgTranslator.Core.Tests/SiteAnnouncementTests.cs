using System.Net;
using System.Text;
using DwgTranslator.Core.Api;
namespace DwgTranslator.Core.Tests;
public sealed class SiteAnnouncementTests
{
    [Theory]
    [InlineData("https://example.test", "https://example.test/v1/site")]
    [InlineData(" https://example.test/v1/ ", "https://example.test/v1/site")]
    [InlineData("https://example.test/api/v1", "https://example.test/api/v1/site")]
    [InlineData("https://dwgc2e-api.maplehousezz.workers.dev", "https://api.cad.pocketter.dpdns.org/v1/site")]
    public void NormalizesEndpoint(string input, string expected) => Assert.Equal(expected, SiteAnnouncementClient.GetEndpoint(input).AbsoluteUri);

    [Theory]
    [InlineData("")][InlineData("ftp://example.test")][InlineData("https://user:password@example.test")]
    [InlineData("https://example.test?secret=x")][InlineData("https://example.test#fragment")]
    public void RejectsInvalidEndpoint(string input) => Assert.Throws<InvalidOperationException>(() => SiteAnnouncementClient.GetEndpoint(input));

    [Theory]
    [InlineData("{\"content\":{\"announcement\":\"更新公告\\n第二行\"}}", "更新公告\n第二行")]
    [InlineData("{\"content\":{\"announcement\":\"\"}}", "")]
    public async Task ReadsPlainPublicContent(string json, string expected)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("/v1/site", request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.CacheControl!.NoCache);
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }));
        Assert.Equal(expected, await SiteAnnouncementClient.ReadAsync(http,"https://example.test/v1/",default));
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"content\":null}")] [InlineData("{\"content\":{\"announcement\":42}}")]
    public async Task MalformedIsNotMistakenForEmptyAnnouncement(string json)
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SiteAnnouncementClient.ReadAsync(http,"https://example.test",default));
    }
    [Fact] public async Task ErrorDoesNotReturnStaleAnnouncement()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => SiteAnnouncementClient.ReadAsync(http,"https://example.test",default));
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
