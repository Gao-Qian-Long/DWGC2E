using System.Net;
using System.Text;
using System.Text.Json;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using Xunit;

namespace DwgTranslator.Core.Tests;

public sealed class CloudGlossaryClientTests
{
    private const string Id = "12345678-1234-4234-8234-123456789abc";
    private static readonly string Revision = new('a', 64);
    private static string Body(string? revision = null, string source = "法兰") => JsonSerializer.Serialize(new
    { success = true, revision = revision ?? Revision, entries = new[] { new { id = Id, source, target = "Flange", note = "网页备注", enabled = false } } });
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static WorkerApiClient Client(HttpClient http, Func<string?>? token = null) => new(http, "https://unit.invalid", token ?? (() => "account-a"), "test", "test");

    [Fact] public async Task DownloadEditUploadPreservesIdentityNoteAndExpectedRevision()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (r, ct) => {
            if (++calls == 1) return Json(Body());
            Assert.Equal(HttpMethod.Put, r.Method);
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            Assert.Equal(Revision, json.RootElement.GetProperty("expected_revision").GetString());
            var item = json.RootElement.GetProperty("entries")[0];
            Assert.Equal(Id, item.GetProperty("id").GetString());
            Assert.Equal("网页备注", item.GetProperty("note").GetString());
            Assert.False(item.GetProperty("enabled").GetBoolean());
            return Json(Body(new string('b', 64), "新原文"));
        }));
        var client = Client(http); var state = await client.ReadCloudGlossaryAsync();
        state.Entries[0].Source = "新原文";
        var saved = await client.SaveCloudGlossaryAsync(state, state.Entries);
        Assert.Equal(new string('b', 64), saved.Revision); Assert.Equal("新原文", saved.Entries[0].Source);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"success\":true,\"entries\":[]}")]
    [InlineData("{\"success\":true,\"revision\":\"bad\",\"entries\":[]}")]
    public async Task MalformedSuccessNeverBecomesEmptyCloudLibrary(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<CloudGlossaryException>(() => Client(http).ReadCloudGlossaryAsync())).Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"id\":\"bad\",\"source\":\"a\",\"target\":\"b\"}")]
    [InlineData("{\"source\":\"a\",\"target\":\"b\"}")]
    [InlineData("{\"id\":\"12345678-1234-4234-8234-123456789abc\",\"source\":\"\",\"target\":\"b\"}")]
    public async Task InvalidRowsFailClosed(string row)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json($"{{\"success\":true,\"revision\":\"{Revision}\",\"entries\":[{row}]}}"))));
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<CloudGlossaryException>(() => Client(http).ReadCloudGlossaryAsync())).Code);
    }

    [Fact] public async Task ConflictNeverReadsNewRevisionOrRetriesAutomatically()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(++calls == 1 ? Json(Body()) : Json("{}", HttpStatusCode.Conflict))));
        var client = Client(http); var state = await client.ReadCloudGlossaryAsync();
        Assert.Equal("glossary_conflict", (await Assert.ThrowsAsync<CloudGlossaryException>(() => client.SaveCloudGlossaryAsync(state, state.Entries))).Code);
        Assert.Equal(2, calls); Assert.Equal(Revision, state.Revision);
    }

    [Fact] public async Task LostWriteResponseRetryKeepsOriginalRevision()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (r, ct) => {
            if (++calls == 1) return Json(Body());
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            Assert.Equal(Revision, json.RootElement.GetProperty("expected_revision").GetString());
            if (calls == 2) throw new HttpRequestException("simulated lost response after commit");
            return Json("{}", HttpStatusCode.Conflict);
        }));
        var client = Client(http); var state = await client.ReadCloudGlossaryAsync();
        Assert.Equal("network_error", (await Assert.ThrowsAsync<CloudGlossaryException>(() => client.SaveCloudGlossaryAsync(state, state.Entries))).Code);
        Assert.Equal("glossary_conflict", (await Assert.ThrowsAsync<CloudGlossaryException>(() => client.SaveCloudGlossaryAsync(state, state.Entries))).Code);
        Assert.Equal(3, calls);
    }

    [Fact] public async Task AccountChangeRejectsOldBasisBeforeSending()
    {
        var token = "a"; var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(Body())); }));
        var client = Client(http, () => token); var state = await client.ReadCloudGlossaryAsync(); token = "b";
        Assert.Equal("session_changed", (await Assert.ThrowsAsync<CloudGlossaryException>(() => client.SaveCloudGlossaryAsync(state, state.Entries))).Code);
        Assert.Equal(1, calls);
    }

    [Fact] public async Task AccountChangeDuringReadDiscardsOldResponse()
    {
        var token = "a";
        using var http = new HttpClient(new Handler((_, _) => { token = "b"; return Task.FromResult(Json(Body())); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http, () => token).ReadCloudGlossaryAsync());
    }

    [Fact] public async Task LegacyBlindUploadNeverSends()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("must not send")));
        Assert.False(await Client(http).PutGlossaryAsync(Array.Empty<CloudGlossaryEntry>()));
    }

    [Fact] public async Task NewRowsOmitNullIdentityAndNote()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (r, ct) => {
            if (++calls == 1) return Json(Body());
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            var item = json.RootElement.GetProperty("entries")[0];
            Assert.False(item.TryGetProperty("id", out _)); Assert.False(item.TryGetProperty("note", out _));
            return Json(Body());
        }));
        var client = Client(http); var state = await client.ReadCloudGlossaryAsync();
        await client.SaveCloudGlossaryAsync(state, new[] { new CloudGlossaryEntry { Source="法兰",Target="Flange",Enabled=false } });
    }

    [Fact] public async Task OversizedReadIsNotTruncatedAndUploadIsRejectedLocally()
    {
        var calls = 0;
        var entries = Enumerable.Range(0, 1001).Select(i => new { id=Guid.NewGuid().ToString(),source=$"s{i}",target="t" });
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(JsonSerializer.Serialize(new {success=true,revision=Revision,entries}))); }));
        var client = Client(http); var state = await client.ReadCloudGlossaryAsync(); Assert.Equal(1001, state.Entries.Count);
        Assert.Equal("invalid_entries", (await Assert.ThrowsAsync<CloudGlossaryException>(() => client.SaveCloudGlossaryAsync(state, state.Entries))).Code);
        Assert.Equal(1, calls);
    }

    [Fact] public void LatestLocalFileAndCloneRetainCloudIdentity()
    {
        var entry = new GlossaryEntry { CloudId=Id,CloudNote="网页备注",Source="a",Target="b" };
        var reloaded = JsonSerializer.Deserialize<GlossaryEntry>(JsonSerializer.Serialize(entry.Clone()))!;
        Assert.Equal(Id,reloaded.CloudId); Assert.Equal("网页备注",reloaded.CloudNote);
    }
    [Fact] public async Task SessionChangedBetweenValidationAndTransportNeverSendsToNewAccount()
    {
        var reads=0;
        using var http = new HttpClient(new Handler((_,_)=>throw new Exception("must not send")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Client(http,()=>++reads==1?"a":"b").ReadCloudGlossaryAsync());
    }

    [Fact] public async Task WriteResponseForDifferentContentCannotConfirmSuccess()
    {
        var calls=0;
        using var http = new HttpClient(new Handler((_,_)=>Task.FromResult(Json(++calls==1?Body():Body(new string('b',64),"different")))));
        var client=Client(http); var state=await client.ReadCloudGlossaryAsync();
        Assert.Equal("invalid_response",(await Assert.ThrowsAsync<CloudGlossaryException>(()=>client.SaveCloudGlossaryAsync(state,state.Entries))).Code);
        Assert.Equal(Revision,state.Revision);
    }

    [Fact] public async Task DuplicateCloudIdentitiesAreRejected()
    {
        using var original=JsonDocument.Parse(Body());
        var row=original.RootElement.GetProperty("entries")[0].GetRawText();
        using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(Json($"{{\"success\":true,\"revision\":\"{Revision}\",\"entries\":[{row},{row}]}}"))));
        await Assert.ThrowsAsync<CloudGlossaryException>(()=>Client(http).ReadCloudGlossaryAsync());
    }

    [Fact] public async Task CallerCancellationIsNotReportedAsCloudSuccess()
    {
        using var source=new CancellationTokenSource();
        using var http=new HttpClient(new Handler(async (_,ct)=>{source.Cancel();await Task.Delay(10000,ct);return Json(Body());}));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Client(http).ReadCloudGlossaryAsync(source.Token));
    }}
