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
            (text, raw) => raw.Replace("倒角", text), () => [new() { Source="倒角", Target="Bevel", SourceLang="ZH", TargetLang="EN" }]);
        var pair = Assert.Single(await service.TranslateBatchAsync([entity], "ZH", "EN"));
        Assert.Empty(api.Requests); Assert.True(pair.GlossaryHit); Assert.True(entity.GlossaryHit);
        Assert.Equal(@"\H2x;Bevel", pair.TranslatedText);
        Assert.Equal(TranslationStatus.Translated, pair.Status);
    }

    [Fact]
    public async Task ChineseTitleBlockTextWithLatinPlaceholdersIsNotMisclassifiedAsAlreadyEnglish()
    {
        const string source = "图号=DWGNAME;项目名称=FILENAME;页数=SHEET;总页数=SHEETMAX";
        var api = new Api();
        var entity = Entity();
        entity.Handle = "FIELD1";
        entity.PlainText = source;
        entity.RawText = source;

        var pair = Assert.Single(await Service(api).TranslateBatchAsync([entity], "ZH", "EN"));

        Assert.Single(api.Requests);
        Assert.Equal(source, Assert.Single(api.Requests[0].Items).Text);
        Assert.Equal(TranslationStatus.Translated, pair.Status);
        Assert.Equal("Chamfer", pair.TranslatedText);
    }
    [Theory]
    [InlineData("EN-ZH", true, false)]
    [InlineData("ZH-EN", true, true)]
    [InlineData("", true, false)]
    [InlineData("ZH-EN", false, false)]
    public async Task GlossaryHonorsDirectionAndEnabled(string direction, bool enabled, bool expected)
    {
        var api=new Api();
        var service=new WorkerTranslationService(api,new AppConfig { GlossaryFirst=true },(t,r)=>t,
            ()=>[new(){Source="倒角",Target="Bevel",SourceLang=direction == "" ? "" : direction.Split('-')[0],TargetLang=direction == "" ? "" : direction.Split('-')[1],Enabled=enabled}]);
        var pair=Assert.Single(await service.TranslateBatchAsync([Entity()],"ZH","EN"));
        Assert.Equal(expected,pair.GlossaryHit); Assert.Equal(expected?0:1,api.Requests.Count);
        if (!expected) Assert.Empty(api.Requests[0].Glossary);
    }
    [Fact]
    public async Task UserTermOverridesSystemButConflictingUserTermsFailExplicitly()
    {
        var api=new Api();
        var terms=new List<GlossaryEntry> {
            new(){Source="倒角",Target="Chamfer",SourceLang="ZH",TargetLang="EN",SourceKind=GlossarySource.System},
            new(){Source="倒角",Target="Bevel",SourceLang="ZH",TargetLang="EN",SourceKind=GlossarySource.User}};
        var service=new WorkerTranslationService(api,new AppConfig {GlossaryFirst=true},(t,r)=>t,()=>terms);
        Assert.Equal("Bevel",(await service.TranslateBatchAsync([Entity()],"ZH","EN"))[0].TranslatedText);
        terms.Add(new(){Source="倒角",Target="Edge bevel",SourceLang="ZH",TargetLang="EN",SourceKind=GlossarySource.User});
        // 冲突只让命中冲突的那一条失败，不再把整批调用炸掉（整张图纸因此全军覆没）。
        var conflicted=Assert.Single(await service.TranslateBatchAsync([Entity()],"ZH","EN"));
        Assert.Equal(TranslationStatus.TranslationFailed,conflicted.Status);
        Assert.Equal("glossary_conflict",conflicted.ErrorMessage);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task ConflictingTermFailsOnlyItsOwnItemsAndKeepsTheRestTranslatable()
    {
        var api=new Api();
        api.Reply=r=>new(){Success=true,Items=r.Items.Select(i=>new TranslationItemResult{Id=i.Id,TranslatedText="Thread"}).ToList()};
        var service=new WorkerTranslationService(api,new AppConfig {BatchSize=10},(t,r)=>t,()=>[
            new(){Source="倒角",Target="Chamfer",SourceLang="ZH",TargetLang="EN"},
            new(){Source="倒角",Target="Edge bevel",SourceLang="ZH",TargetLang="EN"},
            new(){Source="螺纹",Target="Thread",SourceLang="ZH",TargetLang="EN"}]);
        var conflicting=Entity(); conflicting.Handle="A1";
        var embedded=Entity(); embedded.Handle="A2"; embedded.PlainText="倒角处理"; embedded.RawText="倒角处理";
        var clean=Entity(); clean.Handle="A3"; clean.PlainText="螺纹"; clean.RawText="螺纹";

        var pairs=await service.TranslateBatchAsync([conflicting,embedded,clean],"ZH","EN");

        Assert.Equal(3,pairs.Count);
        Assert.All(pairs.Where(p=>p.Handle is "A1" or "A2"),p=>{
            Assert.Equal(TranslationStatus.TranslationFailed,p.Status);
            Assert.Equal("glossary_conflict",p.ErrorMessage); });
        Assert.Equal(TranslationStatus.Translated,pairs.Single(p=>p.Handle=="A3").Status);
        Assert.Equal("Thread",pairs.Single(p=>p.Handle=="A3").TranslatedText);
        // 一条术语都不下发（没有无冲突的命中，且不发冲突条目），整批也不因为冲突被中止。
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task RateLimitedBatchIsRetriedThenOnlyItsOwnChunkFails()
    {
        var attempts=0;
        var api=new Api{Reply=r=>{
            attempts++;
            if(attempts<=5)return new(){Success=false,ErrorCode="rate_limited",Message="请求过于频繁",RetryAfterSeconds=1};
            return new(){Success=true,Items=r.Items.Select(i=>new TranslationItemResult{Id=i.Id,TranslatedText="Chamfer"}).ToList()};}};
        var entities=new List<TextEntity>();
        for(var i=0;i<6;i++){var e=Entity();e.Handle="H"+i;e.PlainText="倒角"+i;e.RawText=e.PlainText;entities.Add(e);}

        var pairs=await Service(api,batchSize:2).TranslateBatchAsync(entities,"ZH","EN");

        // 第一块：首发 + 4 次限流重试（Retry-After=1s 生效）后仍失败 → 只这一块的两条判失败。
        Assert.Equal(2,pairs.Count(p=>p.Status==TranslationStatus.TranslationFailed));
        Assert.All(pairs.Where(p=>p.Status==TranslationStatus.TranslationFailed),p=>Assert.Equal("rate_limited",p.ErrorMessage));
        Assert.Equal(4,pairs.Count(p=>p.Status==TranslationStatus.Translated)); // 后两块照常翻完
        Assert.Equal(7,api.Requests.Count);
    }

    [Fact]
    public async Task RateLimitedChunkThatExhaustsItsBudgetFailsOnlyThatChunk()
    {
        var api=new Api{Reply=r=>{
            // 只让第一块持续限流：后续分块照常成功，用来证明失败被圈在这一块里。
            if(r.Items.Any(i=>i.Text=="倒角0"))
                return new(){Success=false,ErrorCode="rate_limited",Message="请求过于频繁",RetryAfterSeconds=1};
            return new(){Success=true,Items=r.Items.Select(i=>new TranslationItemResult{Id=i.Id,TranslatedText="Chamfer"}).ToList()};}};
        var entities=new List<TextEntity>();
        for(var i=0;i<4;i++){var e=Entity();e.Handle="H"+i;e.PlainText="倒角"+i;e.RawText=e.PlainText;entities.Add(e);}

        var pairs=await Service(api,batchSize:2).TranslateBatchAsync(entities,"ZH","EN");

        // 第一块用尽重试预算后只把这一块标记失败；第二块完全不受影响。
        Assert.True(api.Requests.Count>=3,"requests="+api.Requests.Count
            +" items="+string.Join(",",pairs.Select(p=>p.Handle+":"+p.Status+":"+p.ErrorMessage)));
        Assert.Equal(2,pairs.Count(p=>p.Status==TranslationStatus.TranslationFailed));
        Assert.All(pairs.Where(p=>p.Status==TranslationStatus.TranslationFailed),p=>Assert.Equal("rate_limited",p.ErrorMessage));
        Assert.Equal(2,pairs.Count(p=>p.Status==TranslationStatus.Translated));
        Assert.All(pairs.Where(p=>p.Status==TranslationStatus.Translated),p=>Assert.Equal("Chamfer",p.TranslatedText));
    }

    [Fact]
    public async Task PersistentRateLimitRetriesThenFailsOnlyThoseItems()
    {
        var api=new Api{Reply=_=>new(){Success=false,ErrorCode="rate_limited",Message="请求过于频繁",RetryAfterSeconds=1}};
        var pairs=await Service(api,batchSize:1).TranslateBatchAsync([Entity()],"ZH","EN");

        var failed=Assert.Single(pairs);
        Assert.Equal(TranslationStatus.TranslationFailed,failed.Status);
        Assert.Equal("rate_limited",failed.ErrorMessage);
        Assert.Equal(5,api.Requests.Count); // 首发 + 4 次重试后才放弃，不是发一次就判失败
    }

    [Fact]
    public async Task TerminalBatchErrorIsNotRetried()
    {
        var quota=new Api{Reply=_=>new(){Success=false,ErrorCode="quota_exceeded"}};
        var ex=await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(quota).TranslateBatchAsync([Entity()],"ZH","EN"));
        Assert.Equal("quota_exceeded",ex.Message);
        Assert.Single(quota.Requests); // 额度不足是终态：重试只是浪费，必须立刻上抛
    }

    [Fact]
    public async Task EmptyBatchFailsWithAnExplicitMessage()
    {
        var api=new Api();
        Assert.Contains("worker_empty_input",(await Assert.ThrowsAsync<ArgumentException>(()=>Service(api).TranslateAsync("","ZH","EN"))).Message);
        Assert.Equal(0,api.Requests.Count);
        // 批量路径永不返回 0 条：空输入只会得到零条结果，而不是 LINQ 的 Single() 通用异常。
        Assert.Empty(await Service(api).TranslateBatchAsync([],"ZH","EN"));
        Assert.Equal(0,api.Requests.Count);
    }
    [Fact]
    public async Task EnabledTermsRemainAuthoritativeWhenLegacyPriorityFlagIsOff()
    {
        var api=new Api();
        var service=new WorkerTranslationService(api,new AppConfig {GlossaryFirst=false},(t,r)=>t,
            ()=>[new(){Source="倒角",Target="Bevel",SourceLang="ZH",TargetLang="EN"},new(){Source="螺纹",Target="Thread",SourceLang="ZH",TargetLang="EN"}]);
        var pair=Assert.Single(await service.TranslateBatchAsync([Entity()],"ZH","EN"));
        Assert.True(pair.GlossaryHit);Assert.Equal("Bevel",pair.TranslatedText);Assert.Empty(api.Requests);
    }
    [Fact]
    public async Task PartialGlossaryCannotBeSilentlyIgnoredByServer()
    {
        var api=new Api(); var entity=Entity(); entity.PlainText="倒角处理";
        var service=new WorkerTranslationService(api,new AppConfig(),(t,r)=>t,
            ()=>[new(){Source="倒角",Target="Bevel",SourceLang="ZH",TargetLang="EN"}]);
        var pair=Assert.Single(await service.TranslateBatchAsync([entity],"ZH","EN"));
        Assert.Equal(TranslationStatus.TranslationFailed,pair.Status);
        Assert.Equal("glossary_not_preserved",pair.ErrorMessage);
    }
    [Fact]
    public async Task ShortUserTermWinsOverLongSystemExactMatch()
    {
        var api=new Api { Reply=_=>new(){Success=true,Items=[new(){Id=0,TranslatedText="Large Bevel"}]}};
        var entity=Entity();entity.PlainText="大倒角";
        var service=new WorkerTranslationService(api,new AppConfig(),(t,r)=>t,()=>[
            new(){Source="大倒角",Target="System chamfer",SourceLang="ZH",TargetLang="EN",SourceKind=GlossarySource.System},
            new(){Source="倒角",Target="Bevel",SourceLang="ZH",TargetLang="EN",SourceKind=GlossarySource.User}]);
        var pair=Assert.Single(await service.TranslateBatchAsync([entity],"ZH","EN"));
        Assert.Single(api.Requests);Assert.Equal("Large Bevel",pair.TranslatedText);Assert.True(pair.GlossaryHit);
    }
    [Fact]
    public async Task PrescribedSourceScriptWithinTranslatedSentenceIsNotAnAiEcho()
    {
        var api=new Api { Reply=_=>new(){Success=true,Items=[new(){Id=0,TranslatedText="Install 自定义阀"}]}};
        var entity=Entity();entity.PlainText="安装阀门";
        var service=new WorkerTranslationService(api,new AppConfig(),(t,r)=>t,()=>[
            new(){Source="阀门",Target="自定义阀",SourceLang="ZH",TargetLang="EN"}]);
        var pair=Assert.Single(await service.TranslateBatchAsync([entity],"ZH","EN"));
        Assert.Equal(TranslationStatus.Translated,pair.Status);Assert.Equal("Install 自定义阀",pair.TranslatedText);
    }
}
