using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public class WorkerTranslationServiceTests
{
    private sealed class Api : IApiClient
    {
        public bool IsConfigured => true;
        public string ModeName => "worker";
        public List<TranslationBatchRequest> Requests { get; } = [];
        public Func<TranslationBatchRequest, TranslationBatchResult> Reply { get; set; } = r =>
            new() { Success = true, Items = r.Items.Select(i => new TranslationItemResult { Id = i.Id, TranslatedText = "Chamfer" }).ToList() };
        public Task<TranslationBatchResult> TranslateAsync(TranslationBatchRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult(Reply(request));
        }
        public Task<LoginResult> LoginAsync(string a,string p,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<ProfileInfo?> GetProfileAsync(CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<SubscriptionInfo?> GetSubscriptionAsync(CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<UsageInfo?> GetUsageAsync(CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceInfo>?> GetDevicesAsync(CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<DeviceBindResult> BindDeviceAsync(string a,string b,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<bool> RevokeDeviceAsync(string deviceId,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<VersionInfo?> CheckVersionAsync(string v,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<CloudGlossaryEntry>?> GetGlossaryAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<CloudGlossaryEntry>?>(null);
        public Task<bool> PutGlossaryAsync(IReadOnlyList<CloudGlossaryEntry> e,CancellationToken ct=default)=>Task.FromResult(false);
    }
    private static TextEntity Entity(string path="a.dwg") => new()
    { Handle="A1", SourceFilePath=path, PlainText="倒角", RawText="倒角", Height=2.5, Rotation=Math.PI/2 };
    private static WorkerTranslationService Service(Api api,int batchSize=2) =>
        new(api,new AppConfig{BatchSize=batchSize},(text,raw)=>text);

    [Fact]
    public async Task ChunksAndMapsSameHandleFromDifferentDrawings()
    {
        var api=new Api(); var entities=new List<TextEntity>{Entity("a.dwg"),Entity("b.dwg"),Entity("c.dwg")};
        var pairs=await Service(api).TranslateBatchAsync(entities,"ZH","EN");
        Assert.Equal(new[]{2,1},api.Requests.Select(r=>r.Items.Count));
        Assert.Equal(new[]{"a.dwg","b.dwg","c.dwg"},pairs.Select(p=>p.SourceFilePath));
        Assert.All(entities,e=>Assert.Equal(TranslationStatus.Translated,e.Status));
        Assert.Equal(90,api.Requests[0].Items[0].Rotation,5);
    }
    [Fact]
    public async Task RestoresFormatWithOriginalRawText()
    {
        var api=new Api(); var entity=Entity();entity.RawText=@"H2x;倒角";
        var service=new WorkerTranslationService(api,new AppConfig(),(text,raw)=>{
            Assert.Equal(@"H2x;倒角",raw);return @"H2x;"+text;
        });
        var result=await service.TranslateBatchAsync([entity],"ZH","EN");
        Assert.Equal(@"H2x;Chamfer",result[0].TranslatedText);
        Assert.Equal(result[0].TranslatedText,entity.TranslatedText);
    }
    [Theory]
    [InlineData("倒角")]
    [InlineData("")]
    public async Task RejectsInvalidTranslation(string text)
    {
        var api=new Api{Reply=_=>new(){Success=true,Items=[new(){Id=0,TranslatedText=text}]}};
        var result=await Service(api).TranslateBatchAsync([Entity()],"ZH","EN");
        Assert.Equal(TranslationStatus.TranslationFailed,result[0].Status);
        Assert.Equal("invalid_translation",result[0].ErrorMessage);
    }
    [Fact]
    public async Task MissingResponseIsNotSuccess()
    {
        var api=new Api{Reply=_=>new(){Success=true}};
        Assert.Equal("missing_result",(await Service(api).TranslateBatchAsync([Entity()],"ZH","EN"))[0].ErrorMessage);
    }
    [Fact]
    public async Task BatchErrorStopsWithoutRetry()
    {
        var api=new Api{Reply=_=>new(){Success=false,ErrorCode="quota_exceeded"}};
        var ex=await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(api).TranslateBatchAsync([Entity()],"ZH","EN"));
        Assert.Equal("quota_exceeded",ex.Message);Assert.Single(api.Requests);
    }
    [Fact]
    public async Task CancelledCallDoesNotSendRequest()
    {
        var api=new Api();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service(api).TranslateBatchAsync([Entity()],"ZH","EN",new CancellationToken(true)));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task ExactGlossaryHitStaysLocalAndRestoresCadFormat()
    {
        var api = new Api(); var entity = Entity(); entity.RawText = @"\H2x;倒角";
        var service = new WorkerTranslationService(api, new AppConfig { GlossaryFirst = true },
            (text, raw) => raw.Replace("倒角", text), () => [new() { Source="倒角", Target="Bevel", Direction="ZH-EN" }]);
        var pair = Assert.Single(await service.TranslateBatchAsync([entity], "ZH", "EN"));
        Assert.Empty(api.Requests); Assert.True(pair.GlossaryHit); Assert.True(entity.GlossaryHit);
        Assert.Equal(@"\H2x;Bevel", pair.TranslatedText);
        Assert.Equal(TranslationStatus.Translated, pair.Status);
    }
    [Theory]
    [InlineData("EN-ZH", true, false)]
    [InlineData("ZH-CN-EN-US", true, true)]
    [InlineData("", true, true)]
    [InlineData("ZH-EN", false, false)]
    public async Task GlossaryHonorsDirectionAndEnabled(string direction, bool enabled, bool expected)
    {
        var api=new Api();
        var service=new WorkerTranslationService(api,new AppConfig { GlossaryFirst=true },(t,r)=>t,
            ()=>[new(){Source="倒角",Target="Bevel",Direction=direction,Enabled=enabled}]);
        var pair=Assert.Single(await service.TranslateBatchAsync([Entity()],"ZH","EN"));
        Assert.Equal(expected,pair.GlossaryHit); Assert.Equal(expected?0:1,api.Requests.Count);
        if (!expected) Assert.Empty(api.Requests[0].Glossary);
    }
    [Fact]
    public async Task UserTermOverridesSystemButConflictingUserTermsFailExplicitly()
    {
        var api=new Api();
        var terms=new List<GlossaryEntry> {
            new(){Source="倒角",Target="Chamfer",SourceKind=GlossarySource.System},
            new(){Source="倒角",Target="Bevel",SourceKind=GlossarySource.User}};
        var service=new WorkerTranslationService(api,new AppConfig {GlossaryFirst=true},(t,r)=>t,()=>terms);
        Assert.Equal("Bevel",(await service.TranslateBatchAsync([Entity()],"ZH","EN"))[0].TranslatedText);
        terms.Add(new(){Source="倒角",Target="Edge bevel",SourceKind=GlossarySource.User});
        var pair=Assert.Single(await service.TranslateBatchAsync([Entity()],"ZH","EN"));
        Assert.Equal("glossary_conflict",pair.ErrorMessage);
        Assert.Equal(TranslationStatus.TranslationFailed,pair.Status);Assert.False(pair.GlossaryHit);
        Assert.Empty(api.Requests);
    }
    [Fact]
    public async Task DisabledLocalPriorityStillSuppliesRelevantHintsOnly()
    {
        var api=new Api();
        var service=new WorkerTranslationService(api,new AppConfig {GlossaryFirst=false},(t,r)=>t,
            ()=>[new(){Source="倒角",Target="Bevel"},new(){Source="螺纹",Target="Thread"}]);
        var pair=Assert.Single(await service.TranslateBatchAsync([Entity()],"ZH","EN"));
        Assert.False(pair.GlossaryHit);Assert.Equal("Bevel",Assert.Single(Assert.Single(api.Requests).Glossary).Target);
    }
}
