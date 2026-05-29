using DwgTranslator.Core.Translation;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// Unit tests for FormatCodeParser.
/// Covers all MTEXT/DIMENSION format code combinations.
/// </summary>
public class FormatCodeParserTests
{
    private readonly FormatCodeParser _parser = new();

    // --- Parse Tests ---

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var (plain, template, codes) = _parser.Parse(string.Empty);
        Assert.Empty(plain);
        Assert.Empty(template);
        Assert.Empty(codes);
    }

    [Fact]
    public void Parse_NullString_ReturnsEmpty()
    {
        var (plain, template, codes) = _parser.Parse(null!);
        Assert.Empty(plain);
        Assert.Empty(template);
        Assert.Empty(codes);
    }

    [Fact]
    public void Parse_PlainText_ReturnsSameText()
    {
        var (plain, _, codes) = _parser.Parse("Hello World");
        Assert.Equal("Hello World", plain);
        Assert.Empty(codes);
    }

    [Fact]
    public void Parse_ParagraphCode_ExtractsCorrectly()
    {
        var (plain, _, codes) = _parser.Parse("Line1\\PLine2");
        Assert.Single(codes);
        Assert.Equal("\\P", codes[0]);
        Assert.Equal("Line1__FMT_0__Line2", plain);
    }

    [Fact]
    public void Parse_FontCode_ExtractsDirective()
    {
        var (plain, _, codes) = _parser.Parse("{\\fArial;Bold}Text");
        Assert.Single(codes);
        Assert.Equal("\\fArial;", codes[0]);
        // Text content "Bold" inside brace group is preserved
        Assert.Equal("__FMT_0__BoldText", plain);
    }

    [Fact]
    public void Parse_MultipleFormatCodes_ExtractsAll()
    {
        var (plain, _, codes) = _parser.Parse("{\\fArial;Bold}Title\\PSubtitle");
        Assert.Equal(2, codes.Count);
        Assert.Contains("\\P", codes);
        Assert.Contains("\\fArial;", codes);
        Assert.Equal("__FMT_0__BoldTitle__FMT_1__Subtitle", plain);
    }

    [Fact]
    public void Parse_AlignmentCode_ExtractsCorrectly()
    {
        var (plain, _, codes) = _parser.Parse("\\A1;Centered");
        Assert.Single(codes);
        Assert.Equal("\\A1;", codes[0]);
    }

    [Fact]
    public void Parse_SpecialCharCodes_ExtractsAll()
    {
        var (plain, _, codes) = _parser.Parse("Diameter\\U+00D8");
        Assert.Contains(codes, c => c.Contains("U+"));
    }

    [Fact]
    public void Parse_NestedBraces_ExtractsAllDirectives()
    {
        var (plain, _, codes) = _parser.Parse("{\\fSimHei;\\C3;Important}");
        Assert.Equal(2, codes.Count);
        Assert.Contains("\\fSimHei;", codes);
        Assert.Contains("\\C3;", codes);
        Assert.Equal("__FMT_0____FMT_1__Important", plain);
    }

    [Fact]
    public void Parse_TildeCode_ExtractsCorrectly()
    {
        var (plain, _, codes) = _parser.Parse("Tolerance\\~0.05");
        Assert.Contains(codes, c => c == "\\~");
    }

    [Fact]
    public void Parse_PercentCode_ExtractsCorrectly()
    {
        var (plain, _, codes) = _parser.Parse("50\\%%");
        Assert.Single(codes);
        Assert.Equal("\\%", codes[0]);
    }

    [Fact]
    public void Parse_ComplexMText_ExtractsAllCodes()
    {
        var raw = "{\\fSimHei;\\C3;标题}\\P{\\fArial;\\C1;内容}";
        var (plain, _, codes) = _parser.Parse(raw);
        Assert.Equal(5, codes.Count); // \fSimHei;, \C3;, \P, \fArial;, \C1;
        Assert.Equal("__FMT_0____FMT_1__标题__FMT_2____FMT_3____FMT_4__内容", plain);
    }

    [Fact]
    public void Parse_ObliqueCode_ExtractsCorrectly()
    {
        var (plain, _, codes) = _parser.Parse("{\\fArial;\\Q30;Oblique}");
        Assert.Equal(2, codes.Count);
        Assert.Contains("\\fArial;", codes);
        Assert.Contains("\\Q30;", codes);
    }

    [Fact]
    public void Parse_WidthFactor_ExtractsCorrectly()
    {
        var (plain, _, codes) = _parser.Parse("{\\fArial;\\W1.5;Wide}");
        Assert.Equal(2, codes.Count);
        Assert.Contains("\\fArial;", codes);
        Assert.Contains("\\W1.5;", codes);
    }

    [Fact]
    public void Parse_StackFraction_ExtractsCorrectly()
    {
        var (plain, _, codes) = _parser.Parse("{\\S1/2;}Fraction");
        Assert.Single(codes);
        Assert.Equal("\\S1/2;", codes[0]);
        Assert.Equal("__FMT_0__Fraction", plain);
    }

    // --- Restore Tests ---

    [Fact]
    public void Restore_EmptyCodes_ReturnsTranslated()
    {
        var result = _parser.Restore("Hello", new List<string>());
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void Restore_NullCodes_ReturnsTranslated()
    {
        var result = _parser.Restore("Hello", null!);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void Restore_SingleCode_RestoresCorrectly()
    {
        var codes = new List<string> { "\\P" };
        var result = _parser.Restore("Line1__FMT_0__Line2", codes);
        Assert.Equal("Line1\\PLine2", result);
    }

    [Fact]
    public void Restore_MultipleCodes_RestoresAll()
    {
        var codes = new List<string> { "\\fArial;", "\\P" };
        var result = _parser.Restore("__FMT_0__BoldTitle__FMT_1__Subtitle", codes);
        Assert.Equal("\\fArial;BoldTitle\\PSubtitle", result);
    }

    [Fact]
    public void Restore_UnmatchedPlaceholder_KeepsAsIs()
    {
        var codes = new List<string> { "\\P" };
        var result = _parser.Restore("Text__FMT_5__More", codes);
        Assert.Equal("Text__FMT_5__More", result); // Index 5 doesn't exist
    }

    // --- StripFormatCodes Tests ---

    [Fact]
    public void StripFormatCodes_PlainText_ReturnsSame()
    {
        var result = _parser.StripFormatCodes("Hello World");
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void StripFormatCodes_WithParagraphCode_RemovesIt()
    {
        var result = _parser.StripFormatCodes("Line1\\PLine2");
        Assert.Equal("Line1Line2", result);
    }

    [Fact]
    public void StripFormatCodes_WithFontCode_PreservesText()
    {
        var result = _parser.StripFormatCodes("{\\fArial;Bold}Text");
        Assert.Equal("BoldText", result);
    }

    [Fact]
    public void StripFormatCodes_MultipleCodes_PreservesText()
    {
        var result = _parser.StripFormatCodes("{\\fSimHei;\\C3;标题}\\P内容");
        Assert.Equal("标题内容", result);
    }

    // --- HasFormatCodes Tests ---

    [Fact]
    public void HasFormatCodes_PlainText_ReturnsFalse()
    {
        Assert.False(_parser.HasFormatCodes("Hello"));
    }

    [Fact]
    public void HasFormatCodes_WithCode_ReturnsTrue()
    {
        Assert.True(_parser.HasFormatCodes("Line\\P"));
    }

    [Fact]
    public void HasFormatCodes_EmptyString_ReturnsFalse()
    {
        Assert.False(_parser.HasFormatCodes(string.Empty));
    }

    // --- ValidateFormatCodeIntegrity Tests ---

    [Fact]
    public void ValidateFormatCodeIntegrity_AllCodesPresent_ReturnsTrue()
    {
        var original = "{\\fArial;Bold}Title\\PEnd";
        var translated = "{\\fArial;Bold}Translated Title\\PEnd";
        Assert.True(_parser.ValidateFormatCodeIntegrity(original, translated));
    }

    [Fact]
    public void ValidateFormatCodeIntegrity_MissingCode_ReturnsFalse()
    {
        var original = "{\\fArial;Bold}Title\\PEnd";
        var translated = "Translated Title End";
        Assert.False(_parser.ValidateFormatCodeIntegrity(original, translated));
    }

    [Fact]
    public void ValidateFormatCodeIntegrity_EmptyOriginal_ReturnsTrue()
    {
        Assert.True(_parser.ValidateFormatCodeIntegrity(string.Empty, "anything"));
    }

    // --- Round-trip Tests ---

    [Fact]
    public void RoundTrip_ParseAndRestore_PreservesFormatCodes()
    {
        // Braces are stripped during parse (they're grouping syntax, not content).
        // Format codes are preserved via placeholders.
        var original = "\\fSimHei;\\C3;轴承座\\P公差\\U+00B10.05";
        var (plain, template, codes) = _parser.Parse(original);
        var restored = _parser.Restore(template, codes);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void RoundTrip_SimpleCodes_PreservesAllCodes()
    {
        var original = "\\A1;Centered\\PEnd";
        var (plain, template, codes) = _parser.Parse(original);
        var restored = _parser.Restore(template, codes);
        Assert.Equal(original, restored);
    }
}
