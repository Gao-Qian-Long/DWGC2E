using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// 回归：缓存键必须带上术语表指纹（改完术语表不能再命中旧译文），以及术语冲突只能逐条降级，
/// 不能让整张图纸失败。
/// </summary>
public sealed class GlossaryCacheScopeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dwgc2e-glossary-scope-" + Guid.NewGuid().ToString("N"));

    public GlossaryCacheScopeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Fact]
    public void CacheKeyChangesWhenTheGlossaryChanges()
    {
        var before = EffectiveGlossary.ScopedDirection("ZH", "EN", EffectiveGlossary.Fingerprint([]));
        var after = EffectiveGlossary.ScopedDirection("ZH", "EN", EffectiveGlossary.Fingerprint(
            [new GlossaryEntry { Source = "法兰", Target = "Flange", SourceLang = "ZH", TargetLang = "EN" }]));

        Assert.NotEqual(before, after);
        Assert.NotEqual(
            TranslationConsistencyService.ScopedCacheKey(before, "法兰"),
            TranslationConsistencyService.ScopedCacheKey(after, "法兰"));
    }

    [Fact]
    public void FingerprintIsStableForTheSameTermsAndIndependentOfOrder()
    {
        var flange = new GlossaryEntry { Source = "法兰", Target = "Flange", SourceLang = "ZH", TargetLang = "EN" };
        var valve = new GlossaryEntry { Source = "阀门", Target = "Valve", SourceLang = "ZH", TargetLang = "EN" };

        var a = EffectiveGlossary.Fingerprint([flange, valve]);
        Assert.Equal(a, EffectiveGlossary.Fingerprint([flange, valve]));
        Assert.Equal(a, EffectiveGlossary.Fingerprint([valve, flange]));
        Assert.NotEqual(a, EffectiveGlossary.Fingerprint([flange]));
        Assert.NotEqual(a, EffectiveGlossary.Fingerprint(
            [flange, new GlossaryEntry { Source = "阀门", Target = "Gate valve", SourceLang = "ZH", TargetLang = "EN" }]));
    }

    [Fact]
    public async Task ChangingTheGlossaryAfterTranslationMustNotReuseTheOldCachedText()
    {
        var glossary = new GlossaryService();
        var client = new RecordingClient("Flange");
        var cache = new TranslationConsistencyService(Path.Combine(_directory, "cache.json"));
        var service = new TranslationService(glossary, new FormatCodeParser(), client, "Translate",
            maxRetryCount: 0, consistencyService: cache, maxConcurrency: 1);

        var first = Assert.Single(await service.TranslateBatchAsync([Text("法兰")], "ZH", "EN"));
        Assert.Equal("Flange", first.TranslatedText);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, cache.CacheSize);

        // 用户把术语表改了：同一个源文本的正确答案变了，旧缓存必须失效。
        await glossary.LoadGlossaryAsync(WriteGlossary(
            new GlossaryEntry { Source = "法兰", Target = "Custom flange", SourceLang = "ZH", TargetLang = "EN" }));

        var second = Assert.Single(await service.TranslateBatchAsync([Text("法兰")], "ZH", "EN"));
        Assert.Equal("Custom flange", second.TranslatedText);
        Assert.True(second.GlossaryHit);
        Assert.Equal(1, client.Calls); // 命中术语表，不需要再问模型
    }

    [Fact]
    public async Task GlossaryScopedCacheStillServesRepeatedTextWithinOneGlossaryRevision()
    {
        var glossary = new GlossaryService();
        var client = new RecordingClient("Valve");
        var cache = new TranslationConsistencyService(Path.Combine(_directory, "cache.json"));
        var service = new TranslationService(glossary, new FormatCodeParser(), client, "Translate",
            maxRetryCount: 0, consistencyService: cache, maxConcurrency: 1);

        Assert.Equal("Valve", Assert.Single(await service.TranslateBatchAsync([Text("阀门")], "ZH", "EN")).TranslatedText);
        Assert.Equal("Valve", Assert.Single(await service.TranslateBatchAsync([Text("阀门")], "ZH", "EN")).TranslatedText);
        Assert.Equal(1, client.Calls); // 术语表没变，第二次必须走缓存
    }

    [Fact]
    public async Task CaseVariantsOfOneLabelAreTranslatedOnceAndShareTheResult()
    {
        var glossary = new GlossaryService();
        var client = new RecordingClient("Valve");
        var service = new TranslationService(glossary, new FormatCodeParser(), client, "Translate",
            maxRetryCount: 0, consistencyService: new TranslationConsistencyService(""), maxConcurrency: 1);
        var lower = Text("阀门");
        var upper = Text("阀门");
        upper.Handle = "A2";

        var pairs = await service.TranslateBatchAsync([lower, upper], "ZH", "EN");

        Assert.Equal(2, pairs.Count);
        Assert.Equal(1, client.Calls); // 同一条标签不会因为大小写变体被翻两遍
        Assert.All(pairs, p => Assert.Equal("Valve", p.TranslatedText));
        Assert.All(pairs, p => Assert.Equal(TranslationStatus.Translated, p.Status));
    }

    [Fact]
    public void CacheLookupIsCaseInsensitiveLikeGlossaryMatching()
    {
        var cache = new TranslationConsistencyService(Path.Combine(_directory, "case.json"));
        cache.AddToCache("Valve Body", "阀体", "ZH>EN");

        Assert.True(cache.TryGetMatch("VALVE BODY", "ZH>EN", out var variant));
        Assert.Equal("阀体", variant);
        Assert.Equal(1, cache.CacheSize);
    }

    [Fact]
    public async Task ConflictingTermsFailOnlyTheItemsThatHitThem()
    {
        var terms = new List<GlossaryEntry>
        {
            new() { Source = "倒角", Target = "Chamfer", SourceLang = "ZH", TargetLang = "EN" },
            new() { Source = "倒角", Target = "Edge bevel", SourceLang = "ZH", TargetLang = "EN" },
            new() { Source = "阀门", Target = "Valve", SourceLang = "ZH", TargetLang = "EN" }
        };
        var api = new BatchApi();
        var service = new WorkerTranslationService(api, new AppConfig { BatchSize = 10 }, (text, _) => text, () => terms);

        var conflicting = new TextEntity { Handle = "A1", PlainText = "倒角", RawText = "倒角" };
        var clean = new TextEntity { Handle = "A2", PlainText = "阀门", RawText = "阀门" };

        var pairs = await service.TranslateBatchAsync([conflicting, clean], "ZH", "EN");

        var failed = Assert.Single(pairs, p => p.Handle == "A1");
        Assert.Equal(TranslationStatus.TranslationFailed, failed.Status);
        Assert.Equal("glossary_conflict", failed.ErrorMessage);
        var translated = Assert.Single(pairs, p => p.Handle == "A2");
        Assert.Equal(TranslationStatus.Translated, translated.Status);
        Assert.Equal("Valve", translated.TranslatedText);
        Assert.Empty(api.Requests); // 无冲突的那条直接命中术语表，冲突那条不下发
    }

    private sealed class BatchApi : DwgTranslator.Core.Api.IApiClient
    {
        public List<DwgTranslator.Core.Api.TranslationBatchRequest> Requests { get; } = [];
        public bool IsConfigured => true;
        public string ModeName => "worker";
        public Task<DwgTranslator.Core.Api.TranslationBatchResult> TranslateAsync(
            DwgTranslator.Core.Api.TranslationBatchRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DwgTranslator.Core.Api.TranslationBatchResult
            {
                Success = true,
                Items = request.Items.Select(i => new DwgTranslator.Core.Api.TranslationItemResult { Id = i.Id, TranslatedText = "Api" }).ToList()
            });
        }
        public Task<DwgTranslator.Core.Api.LoginResult> LoginAsync(string a, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DwgTranslator.Core.Api.ProfileInfo?> GetProfileAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DwgTranslator.Core.Api.SubscriptionInfo?> GetSubscriptionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DwgTranslator.Core.Api.UsageInfo?> GetUsageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DwgTranslator.Core.Api.DeviceInfo>?> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DwgTranslator.Core.Api.DeviceBindResult> BindDeviceAsync(string a, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DwgTranslator.Core.Api.VersionInfo?> CheckVersionAsync(string v, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudGlossaryEntry>?> GetGlossaryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CloudGlossaryEntry>?>(null);
        public Task<bool> PutGlossaryAsync(IReadOnlyList<CloudGlossaryEntry> e, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    public void LegacyDirectionlessCacheKeysStillMigrateIntoTheZhToEnScope()
    {
        var path = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(path, "{\"法兰\":\"Legacy flange\"}");
        var cache = new TranslationConsistencyService(path);

        Assert.True(cache.TryGetMatch("法兰", "ZH>EN", out var migrated));
        Assert.Equal("Legacy flange", migrated);
        Assert.False(cache.TryGetMatch("法兰", "EN>ZH", out _));
        // 术语表指纹参与作用域后，旧译文不会被"带指纹的新翻译"复用。
        Assert.False(cache.TryGetMatch("法兰", EffectiveGlossary.ScopedDirection("ZH", "EN", "0123456789ABCDEF"), out _));
    }

    private static TextEntity Text(string plain) => new()
    {
        Handle = "A1", SourceFilePath = "drawing.dwg", PlainText = plain, RawText = plain, Height = 2.5
    };

    private string WriteGlossary(params GlossaryEntry[] entries)
    {
        var path = Path.Combine(_directory, "glossary-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(entries));
        return path;
    }

    private sealed class RecordingClient(string reply) : IDeepSeekClient
    {
        public int Calls;
        public Task<string> ChatCompletionAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(reply);
        }
    }
}
