using System.Net;
using System.Text;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;

namespace DwgTranslator.Core.Tests;

public sealed class TranslationContextVersionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-context-version-" + Guid.NewGuid().ToString("N"));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static WorkerApiClient Client(HttpClient http) =>
        new(http, "https://worker.invalid", () => "session-token", "device", "host");

    [Fact]
    public async Task ContextEndpointRefreshesTrimmedCachedVersion()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("/v1/translation-context", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"context_version":"  policy-v7  "}"""));
        }));
        var client = Client(http);

        Assert.Equal("policy-v7", await client.RefreshTranslationContextVersionAsync());
        Assert.Equal("policy-v7", client.CachedTranslationContextVersion);
    }

    [Fact]
    public async Task TranslateResponseAlsoAdvancesCachedVersion()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(
            """{"success":true,"context_version":"policy-v8","items":[{"id":1,"translated_text":"Valve"}]}"""))));
        var client = Client(http);

        var result = await client.TranslateAsync(new TranslationBatchRequest
        {
            SourceLang = "ZH", TargetLang = "EN", Items = [new TranslationItem { Id = 1, Text = "阀门" }]
        });

        Assert.True(result.Success);
        Assert.Equal("policy-v8", client.CachedTranslationContextVersion);
    }

    [Fact]
    public async Task NetworkFailureKeepsStableFallbackAndReturnsNull()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline")));
        var client = Client(http);
        var before = client.CachedTranslationContextVersion;

        Assert.Null(await client.RefreshTranslationContextVersionAsync());
        Assert.Equal(before, client.CachedTranslationContextVersion);
    }

    [Fact]
    public async Task UnauthorizedSessionIsRejectedAndNotifiedOnlyOnce()
    {
        var sends = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            sends++;
            return Task.FromResult(Json("""{"error_code":"token_expired","message":"expired"}""", HttpStatusCode.Unauthorized));
        }));
        var client = Client(http);
        var notices = new List<string>();
        client.AuthenticationRejected += failure => notices.Add(failure.ErrorCode);

        var first = await Assert.ThrowsAsync<ApiAuthenticationException>(() => client.RefreshTranslationContextVersionAsync());
        var second = await Assert.ThrowsAsync<ApiAuthenticationException>(() => client.RefreshTranslationContextVersionAsync());

        Assert.Equal("token_expired", first.ErrorCode);
        Assert.Equal("token_expired", second.ErrorCode);
        Assert.Equal(1, sends);
        Assert.Equal(["token_expired"], notices);
    }

    [Fact]
    public void CheckpointSignatureChangesOnlyForTranslationSemantics()
    {
        Directory.CreateDirectory(root);
        var drawing = Path.Combine(root, "drawing.dwg");
        File.WriteAllText(drawing, "stable fixture");
        var config = new AppConfig
        {
            SourceLanguage = "ZH", TargetLanguage = "EN",
            ProtectDimensions = true, ProtectTolerances = true, ProtectModels = true, GlossaryFirst = true,
            ExportDirectory = Path.Combine(root, "out-a"), OutputNamingPattern = "{name}_a",
            MaxTranslationConcurrency = 2, BatchSize = 10, Language = "zh-CN"
        };
        var translator = new SemanticTranslator { ContextVersion = "policy-v1", GlossaryVersion = "terms-v1" };
        using var manager = new TaskManager(new Reader(), null, translator, new Writer(), null,
            new Store(), new TaskManagerOptions { LocalWorkerCount = 1, AiConcurrency = 1 }, config);
        manager.ConfigureRun("ZH", "EN");
        var baseline = manager.BuildCheckpointSignature(drawing);

        config.ExportDirectory = Path.Combine(root, "out-b");
        config.OutputNamingPattern = "{name}_b";
        config.MaxTranslationConcurrency = 20;
        config.BatchSize = 99;
        config.Language = "en-US";
        manager.Options.LocalWorkerCount = 6;
        manager.Options.AiConcurrency = 8;
        Assert.Equal(baseline, manager.BuildCheckpointSignature(drawing));

        translator.ContextVersion = "policy-v2";
        var policyChanged = manager.BuildCheckpointSignature(drawing);
        Assert.NotEqual(baseline, policyChanged);

        translator.ContextVersion = "policy-v1";
        translator.GlossaryVersion = "terms-v2";
        Assert.NotEqual(baseline, manager.BuildCheckpointSignature(drawing));

        translator.GlossaryVersion = "terms-v1";
        config.ProtectDimensions = false;
        Assert.NotEqual(baseline, manager.BuildCheckpointSignature(drawing));

        config.ProtectDimensions = true;
        manager.ConfigureRun("EN", "ZH");
        Assert.NotEqual(baseline, manager.BuildCheckpointSignature(drawing));
    }

    [Fact]
    public void CheckpointIsInvalidatedWhenTheDrawingContentChanges()
    {
        Directory.CreateDirectory(root);
        var drawing = Path.Combine(root, "changed.dwg");
        File.WriteAllText(drawing, "first revision");
        var config = new AppConfig { SourceLanguage = "ZH", TargetLanguage = "EN", ExportDirectory = root };
        var translator = new SemanticTranslator { ContextVersion = "policy-v1", GlossaryVersion = "terms-v1" };
        using var manager = new TaskManager(new Reader(), null, translator, new Writer(), null,
            new Store(), new TaskManagerOptions { LocalWorkerCount = 1, AiConcurrency = 1 }, config);
        manager.ConfigureRun("ZH", "EN");

        var baseline = manager.BuildCheckpointSignature(drawing);
        // Identical bytes on disk must reuse the checkpoint, otherwise every rerun re-translates.
        Assert.Equal(baseline, manager.BuildCheckpointSignature(drawing));

        File.WriteAllText(drawing, "second revision with edited dimensions");
        Assert.NotEqual(baseline, manager.BuildCheckpointSignature(drawing));
    }

    private sealed class SemanticTranslator : ITranslationService, ITranslationCheckpointContextProvider
    {
        public string ContextVersion { get; set; } = string.Empty;
        public string GlossaryVersion { get; set; } = string.Empty;
        public string GetCheckpointContext(string sourceLanguage, string targetLanguage) =>
            $"{TranslationLanguages.Normalize(sourceLanguage)}|{TranslationLanguages.Normalize(targetLanguage)}|{ContextVersion}|{GlossaryVersion}";
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<TranslationPair>> TranslateBatchAsync(List<TextEntity> entities, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<TranslationPair>> TranslateBatchWithProgressAsync(List<TextEntity> entities, string sourceLanguage, string targetLanguage, IProgress<TranslationPair>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Reader : IDwgReaderService
    {
        public List<TextEntity> ExtractFromFile(string filePath) => [];
        public Dictionary<string, List<TextEntity>> ExtractFromFiles(IEnumerable<string> filePaths) => new();
    }

    private sealed class Writer : IDwgWriterService
    {
        public CadWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<TextEntity> entities, bool targetIsCjk = true, CancellationToken cancellationToken = default, WritebackOptions? options = null) => new();
    }

    private sealed class Store : ITaskStore
    {
        public IReadOnlyList<TranslationTask> Load() => [];
        public void Save(IEnumerable<TranslationTask> tasks) { }
        public void Clear() { }
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("dwgc2e-context-version-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture directory");
        Directory.Delete(full, true);
    }
}
