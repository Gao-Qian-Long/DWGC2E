using DwgTranslator.Cad.Replacement;
namespace DwgTranslator.Core.Tests;
public class MTextFitFormattingTests
{
    [Fact] public void AbsoluteHeightOverridesScaleWithEntityHeight() =>
        Assert.Equal(@"\W1.0732;\H0.95x;Common parts register\H1x;", MTextFitFormatting.NormalizeHeights(@"\W1.0732;\H190.00;Common parts register\H200.00;",200));
    [Fact] public void RelativeHeightsAndEscapedCommandsRemainUnchanged() =>
        Assert.Equal(@"\H0.8x;{\H1.2X;A}\\H190;", MTextFitFormatting.NormalizeHeights(@"\H0.8x;{\H1.2X;A}\\H190;",200));
    [Fact] public void NestedWidthOverridesCannotCancelCondensation() =>
        Assert.Equal(@"{\W0.75;{\W0.75;A{\W0.375;B}}C}", MTextFitFormatting.ScaleWidths(@"{\W1;A{\W0.5;B}}C",.75));
    [Fact] public void LiteralAndOtherFormattingPreserved() =>
        Assert.Equal(@"{\W0.5;\\W1;\C3;\H0.8x;A\PB}", MTextFitFormatting.ScaleWidths(@"\\W1;\C3;\H0.8x;A\PB",.5));
    [Fact] public void InvalidHeightReferenceDoesNotRewrite() =>
        Assert.Equal(@"\H190;A",MTextFitFormatting.NormalizeHeights(@"\H190;A",0));
}
