using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Text.Json;
using Xunit;
using DwgTranslator.Core.Translation;
namespace DwgTranslator.Core.Tests;
public sealed class AuthoritativeGlossaryTests
{
    private sealed class Client(string reply) : IDeepSeekClient
    {
        public int Calls;
        public Task<string> ChatCompletionAsync(string systemPrompt,string userMessage,CancellationToken ct=default)
        { Calls++; return Task.FromResult(reply); }
    }
    [Theory]
    [InlineData("法兰", "Custom flange", "Old flange")]
    [InlineData("Valve", "自定义阀", "Old translation")]
    public async Task EnabledTermWinsBeforeCacheOrLanguageSkip(string source,string target,string cached)
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try {
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new[]{new GlossaryEntry {Source=source,Target=target,SourceLang="ZH",TargetLang="EN"}}));
            var glossary=new GlossaryService(); await glossary.LoadGlossaryAsync(path);
            var cache=new TranslationConsistencyService("");cache.AddToCache(source,cached,"ZH>EN");
            var client=new Client("Wrong");var service=new TranslationService(glossary,new FormatCodeParser(),client,"Translate",maxRetryCount:0,consistencyService:cache);
            // Exact prescribed wording is allowed even when the target contains the source script.
            { var pair=Assert.Single(await service.TranslateBatchAsync([new(){Handle="1",PlainText=source}],"ZH","EN"));Assert.Equal(target,pair.TranslatedText);Assert.True(pair.GlossaryHit); }
            Assert.Equal(0,client.Calls);
        } finally {File.Delete(path);}
    }
    [Fact]
    public async Task MissingPlaceholderDoesNotProduceSuccessfulOutput()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try {
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new[]{new GlossaryEntry{Source="法兰",Target="Custom flange",SourceLang="ZH",TargetLang="EN"}}));
            var glossary=new GlossaryService();await glossary.LoadGlossaryAsync(path);
            var service=new TranslationService(glossary,new FormatCodeParser(),new Client("Install flange"),"Translate",maxRetryCount:0);
            await Assert.ThrowsAsync<InvalidDataException>(()=>service.TranslateAsync("安装法兰","ZH","EN"));
        } finally {File.Delete(path);}
    }
}
