using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public class TextEnvelopeGeometryTests
{
    [Theory]
    [InlineData(-2, 4, 0, 10, 2)]
    [InlineData(8, 12, 0, 10, -2)]
    [InlineData(2, 8, 0, 10, 0)]
    [InlineData(-2, 12, 0, 10, 0)]
    [InlineData(15, 25, 0, 10, -15)]
    public void FittingBoxesMoveMinimallyAndOversizeBoxesDoNotMove(double min, double max,
        double originalMin, double originalMax, double expected)
        => Assert.Equal(expected, TextEnvelopeGeometry.FittingAxisOffset(min, max, originalMin, originalMax));

    [Fact]
    public void ExtractedCalculationsMatchLegacyFormulaForDeterministicSamples()
    {
        var random = new Random(20260915);
        for (int i = 0; i < 10000; i++)
        {
            double min = random.NextDouble() * 400 - 200, max = min + random.NextDouble() * 100;
            double lo = random.NextDouble() * 400 - 200, hi = lo + random.NextDouble() * 100;
            double expected = 0;
            if (max - min <= hi - lo)
                expected = min < lo ? lo - min : max > hi ? hi - max : 0;
            Assert.Equal(expected, TextEnvelopeGeometry.FittingAxisOffset(min, max, lo, hi));
            Assert.Equal(min >= lo - 0.01 && max <= hi + 0.01,
                TextEnvelopeGeometry.IsAxisInside(min, max, lo, hi));
            double angle = (random.NextDouble() - 0.5) * 1000;
            double legacy = angle % Math.PI;
            if (legacy < 0) legacy += Math.PI;
            Assert.Equal(legacy, TextEnvelopeGeometry.NormalizeHalfTurn(angle));
        }
    }

    [Fact]
    public void ContainmentRetainsOriginalTolerance()
    {
        Assert.True(TextEnvelopeGeometry.IsAxisInside(-0.01, 10.01, 0, 10));
        Assert.False(TextEnvelopeGeometry.IsAxisInside(-0.01001, 10, 0, 10));
        Assert.False(TextEnvelopeGeometry.IsAxisInside(0, 10.01001, 0, 10));
        Assert.Equal(0, TextEnvelopeGeometry.NormalizeHalfTurn(-Math.PI));
    }
}
