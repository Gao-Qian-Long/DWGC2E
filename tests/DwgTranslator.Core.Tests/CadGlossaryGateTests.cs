using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
namespace DwgTranslator.Core.Tests;
public sealed class CadGlossaryGateTests
{
    private static readonly GlossaryEntry[] Terms = [new() { Source="江苏省常州市武进区横林镇", Target="上海市闵行区中春路7001号2幢2楼2028室", SourceLang="ZH", TargetLang="EN" }];
    [Theory]
    [InlineData("上海市闵行区中春路7001号2幢2楼2028室", true)]
    [InlineData("江苏省常州市武进区横林镇", false)]
    [InlineData("另一个中文地址", false)]
    [InlineData("__FMT_0__上海市闵行区中春路7001号2幢2楼2028室", false)]
    public void OnlyExplicitPrescribedWordingPasses(string target, bool expected) =>
        Assert.Equal(expected, TranslationQualityValidator.IsAcceptableCadText(Terms[0].Source, @"\f宋体|b0|i0;"+target,"ZH","EN",Terms));
    [Fact]
    public void PrescribedTermDoesNotAllowUntranslatedSurroundingText() =>
        Assert.False(TranslationQualityValidator.IsAcceptableCadText("地址："+Terms[0].Source, "地址："+Terms[0].Target,"ZH","EN",Terms));
    [Fact]
    public void NoGlossaryStillRejectsChinese() =>
        Assert.False(TranslationQualityValidator.IsAcceptableCadText(Terms[0].Source, Terms[0].Target,"ZH","EN"));
}
