using DwgTranslator.Core.Models;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// Unit tests for TextEntity model.
/// </summary>
public class TextEntityModelTests
{
    [Fact]
    public void TextEntity_DefaultValues_AreCorrect()
    {
        var entity = new TextEntity();

        Assert.Equal(string.Empty, entity.Handle);
        Assert.Equal(string.Empty, entity.RawText);
        Assert.Equal(string.Empty, entity.PlainText);
        Assert.Equal(string.Empty, entity.FormatTemplate);
        Assert.Equal(string.Empty, entity.EntityType);
        Assert.Equal(0, entity.Height);
        Assert.Equal(0, entity.Rotation);
        Assert.Equal(string.Empty, entity.TextStyleName);
        Assert.Equal(string.Empty, entity.BlockName);
        Assert.False(entity.IsXref);
        Assert.Equal(TranslationStatus.Pending, entity.Status);
        Assert.Equal(string.Empty, entity.TranslatedText);
        Assert.False(entity.GlossaryHit);
        Assert.Equal(string.Empty, entity.Notes);
    }

    [Fact]
    public void TextEntity_SetProperties_Works()
    {
        var entity = new TextEntity
        {
            Handle = "1A2B",
            RawText = "\\P轴承座",
            PlainText = "轴承座",
            EntityType = "MText",
            Height = 2.5,
            Rotation = 0.785,
            TextStyleName = "Standard",
            BlockName = "MyBlock",
            IsXref = false,
            Status = TranslationStatus.Translated,
            TranslatedText = "\\PBearing Housing",
            GlossaryHit = true,
            Notes = "Test note"
        };

        Assert.Equal("1A2B", entity.Handle);
        Assert.Equal("MText", entity.EntityType);
        Assert.Equal(2.5, entity.Height);
        Assert.True(entity.GlossaryHit);
    }

    [Fact]
    public void Point3d_Constructor_SetsValues()
    {
        var p = new Point3d(1.0, 2.0, 3.0);

        Assert.Equal(1.0, p.X);
        Assert.Equal(2.0, p.Y);
        Assert.Equal(3.0, p.Z);
    }

    [Fact]
    public void TranslationStatus_AllValues_Exist()
    {
        var values = Enum.GetValues<TranslationStatus>();

        Assert.Contains(TranslationStatus.Pending, values);
        Assert.Contains(TranslationStatus.GlossaryMatched, values);
        Assert.Contains(TranslationStatus.Translated, values);
        Assert.Contains(TranslationStatus.TranslationFailed, values);
        Assert.Contains(TranslationStatus.Reviewed, values);
        Assert.Contains(TranslationStatus.WritebackSuccess, values);
        Assert.Contains(TranslationStatus.WritebackFailed, values);
        Assert.Contains(TranslationStatus.Skipped, values);
    }
}
