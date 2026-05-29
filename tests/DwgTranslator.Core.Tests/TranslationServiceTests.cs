using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using Moq;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// Unit tests for TranslationService with mocked DeepSeek client.
/// </summary>
public class TranslationServiceTests
{
    private readonly Mock<IGlossaryService> _mockGlossaryService;
    private readonly Mock<IDeepSeekClient> _mockDeepSeekClient;
    private readonly FormatCodeParser _formatCodeParser;
    private const string SystemPrompt = "You are a professional mechanical engineering translator.";

    public TranslationServiceTests()
    {
        _mockGlossaryService = new Mock<IGlossaryService>();
        _mockDeepSeekClient = new Mock<IDeepSeekClient>();
        _formatCodeParser = new FormatCodeParser();

        // Default glossary setup
        _mockGlossaryService.Setup(g => g.MatchTerms(It.IsAny<string>()))
            .Returns(new List<GlossaryMatch>());
        _mockGlossaryService.Setup(g => g.ReplaceWithPlaceholders(It.IsAny<string>(), It.IsAny<List<GlossaryMatch>>()))
            .Returns((string text, List<GlossaryMatch> m) => text);
        _mockGlossaryService.Setup(g => g.RestorePlaceholders(It.IsAny<string>(), It.IsAny<List<GlossaryMatch>>()))
            .Returns((string text, List<GlossaryMatch> m) => text);
    }

    [Fact]
    public async Task TranslateAsync_PlainText_CallsTranslator()
    {
        _mockDeepSeekClient.Setup(c => c.ChatCompletionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hello");

        var service = new TranslationService(
            _mockGlossaryService.Object, _formatCodeParser, _mockDeepSeekClient.Object, SystemPrompt);

        var result = await service.TranslateAsync("你好", "ZH", "EN");

        Assert.Equal("Hello", result);
        _mockDeepSeekClient.Verify(c => c.ChatCompletionAsync(
            SystemPrompt, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TranslateAsync_WithGlossaryMatch_AppliesGlossary()
    {
        var matches = new List<GlossaryMatch>
        {
            new() { SourceTerm = "轴承", Placeholder = "__GLOSSARY_1__", TargetTerm = "Bearing", Position = 0 }
        };
        _mockGlossaryService.Setup(g => g.MatchTerms("轴承座"))
            .Returns(matches);
        _mockGlossaryService.Setup(g => g.ReplaceWithPlaceholders("轴承座", matches))
            .Returns("__GLOSSARY_1__座");
        _mockGlossaryService.Setup(g => g.RestorePlaceholders("__GLOSSARY_1__ Housing", matches))
            .Returns("Bearing Housing");

        _mockDeepSeekClient.Setup(c => c.ChatCompletionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("__GLOSSARY_1__ Housing");

        var service = new TranslationService(
            _mockGlossaryService.Object, _formatCodeParser, _mockDeepSeekClient.Object, SystemPrompt);

        var result = await service.TranslateAsync("轴承座", "ZH", "EN");

        Assert.Equal("Bearing Housing", result);
    }

    [Fact]
    public async Task TranslateAsync_WithFormatCodes_PreservesThem()
    {
        _mockDeepSeekClient.Setup(c => c.ChatCompletionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Tolerance\\P±0.05");

        var service = new TranslationService(
            _mockGlossaryService.Object, _formatCodeParser, _mockDeepSeekClient.Object, SystemPrompt);

        var result = await service.TranslateAsync("公差\\P±0.05", "ZH", "EN");

        Assert.Contains("\\P", result);
    }

    [Fact]
    public async Task TranslateAsync_EmptyText_ReturnsEmpty()
    {
        var service = new TranslationService(
            _mockGlossaryService.Object, _formatCodeParser, _mockDeepSeekClient.Object, SystemPrompt);

        var result = await service.TranslateAsync(string.Empty, "ZH", "EN");

        Assert.Empty(result);
    }

    [Fact]
    public async Task TranslateAsync_TranslatorFails_RetriesAndThrows()
    {
        _mockDeepSeekClient.Setup(c => c.ChatCompletionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("API Error"));

        var service = new TranslationService(
            _mockGlossaryService.Object, _formatCodeParser, _mockDeepSeekClient.Object, SystemPrompt, maxRetryCount: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TranslateAsync("test", "ZH", "EN"));

        // Should have retried 3 times (initial + 2 retries)
        _mockDeepSeekClient.Verify(c => c.ChatCompletionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task TranslateBatchAsync_MultipleEntities_ProcessesInBatches()
    {
        var entities = new List<TextEntity>
        {
            new() { Handle = "1", PlainText = "Text1" },
            new() { Handle = "2", PlainText = "Text2" },
            new() { Handle = "3", PlainText = "Text3" }
        };

        _mockDeepSeekClient.Setup(c => c.ChatCompletionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Translated");

        var service = new TranslationService(
            _mockGlossaryService.Object, _formatCodeParser, _mockDeepSeekClient.Object, SystemPrompt, batchSize: 2);

        var results = await service.TranslateBatchAsync(entities, "ZH", "EN");

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(TranslationStatus.Translated, r.Status));
    }

    [Fact]
    public async Task TranslateBatchAsync_PartialFailure_ContinuesProcessing()
    {
        var entities = new List<TextEntity>
        {
            new() { Handle = "1", RawText = "Text1", PlainText = "Text1" },
            new() { Handle = "2", RawText = "Text2", PlainText = "Text2" }
        };

        // First two calls fail (entity 1 initial + retry), third call succeeds (entity 2)
        _mockDeepSeekClient.SetupSequence(c => c.ChatCompletionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("API Error"))
            .ThrowsAsync(new Exception("API Error"))
            .ReturnsAsync("OK");

        var service = new TranslationService(
            _mockGlossaryService.Object, _formatCodeParser, _mockDeepSeekClient.Object, SystemPrompt,
            maxRetryCount: 1);

        var results = await service.TranslateBatchAsync(entities, "ZH", "EN");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Status == TranslationStatus.TranslationFailed);
        Assert.Contains(results, r => r.Status == TranslationStatus.Translated);
    }
}
