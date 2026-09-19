using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class GlossaryProjectApplicationTests
{
    private static readonly IReadOnlyList<GlossaryEntry> Terms = new[]
    {
        new GlossaryEntry { Source = "阀门", Target = "valve", SourceLang = "zh", TargetLang = "en", SourceKind = GlossarySource.User },
        new GlossaryEntry { Source = "管道", Target = "pipeline", SourceLang = "zh", TargetLang = "en", SourceKind = GlossarySource.Enterprise }
    };

    [Fact]
    public void ExactSourceTermReplacesWholeTranslation()
    {
        var result = EffectiveGlossary.ApplyToExistingTranslation(" 阀门 ", "old wording", Terms, out var hits);
        Assert.Equal("valve", result);
        Assert.Equal(1, hits);
    }

    [Fact]
    public void EmbeddedTermOnlyReplacesWhenSourceTokenRemainsInTranslation()
    {
        var safe = EffectiveGlossary.ApplyToExistingTranslation("关闭阀门", "Close 阀门", Terms, out var safeHits);
        var unsafeMapping = EffectiveGlossary.ApplyToExistingTranslation("关闭阀门", "Close the component", Terms, out var unsafeHits);
        Assert.Equal("Close valve", safe);
        Assert.Equal("Close the component", unsafeMapping);
        Assert.Equal(1, safeHits);
        Assert.Equal(1, unsafeHits);
    }

    [Fact]
    public void MultipleTermsAreAppliedWithoutGuessingTranslatedPositions()
    {
        var result = EffectiveGlossary.ApplyToExistingTranslation("阀门连接管道", "阀门 connects to 管道", Terms, out var hits);
        Assert.Equal("valve connects to pipeline", result);
        Assert.Equal(2, hits);
    }
}
