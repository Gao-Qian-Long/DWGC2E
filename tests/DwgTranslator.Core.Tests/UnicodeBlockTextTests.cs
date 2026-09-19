using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;

namespace DwgTranslator.Core.Tests;

public sealed class UnicodeBlockTextTests
{
    [Theory]
    [InlineData(".dwg")]
    [InlineData(".dxf")]
    public void EncodedChineseInNestedBlocksIsReadableAndSourceFormattingRemainsUntouched(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "dwgc2e-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var doc = new CadDocument();
            var child = new BlockRecord("TabularNote1");
            doc.BlockRecords.Add(child);
            var samples = new[] { "材质：", "设 计", "江苏欧西克智能机械有限公司", "图纸编号：0.包装机上料机械手L8850-W1650" };
            var raws = samples.Select(s => @"\f宋体|b0|i0;\W0.6000;" + Encode(s)).ToArray();
            foreach (var raw in raws) child.Entities.Add(new MText { Value = raw, Height = 5 });
            child.Entities.Add(new ACadSharp.Entities.TextEntity { Value = Encode("材质"), Height = 5 });
            doc.Entities.Add(new Insert(child));
            doc.Entities.Add(new Insert(child));
            var path = Path.Combine(root, "source" + extension);
            if (extension == ".dwg") DwgWriter.Write(path, doc); else DxfWriter.Write(path, doc, false);
            var before = File.ReadAllBytes(path);
            var items = new DwgReaderService().ExtractFromFile(path);
            Assert.Equal(5, items.Count);
            foreach (var (raw, expected) in raws.Zip(samples))
            {
                var item = Assert.Single(items.Where(x => x.RawText == raw));
                Assert.Equal(expected, item.PlainText);
                Assert.Equal(raw, item.FormatTemplate);
            }
            Assert.Equal("材质", Assert.Single(items.Where(x => x.EntityType == "DBText")).PlainText);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnicodeCharactersAreNotRestoredAsOriginalChineseAroundEnglish()
    {
        var raw = @"\f宋体|b0|i0;\W0.6000;\U+6750\U+8D28\U+FF1A";
        var parser = new FormatCodeParser();
        var parsed = parser.Parse(raw);
        Assert.Contains("材质：", parsed.FormatTemplate);
        Assert.DoesNotContain(parsed.FormatCodes, x => x.StartsWith(@"\U+"));
        var service = new TranslationService(new GlossaryService(), parser, new UnusedClient(), "Translate");
        var restored = service.RestoreFormatCodes("Material:", raw);
        Assert.Equal(@"\f宋体|b0|i0;\W0.6000;Material:", restored);
        Assert.Equal("材质：", parser.StripFormatCodes(raw));
    }

    [Theory]
    [InlineData(@"\U+6750", "材")]
    [InlineData(@"\\U+6750", @"\\U+6750")]
    [InlineData(@"\U+ZZZZ", @"\U+ZZZZ")]
    [InlineData(@"\U+123", @"\U+123")]
    [InlineData("U+6750", "U+6750")]
    [InlineData(@"\U+D83D\U+DE00", "😀")]
    public void DecoderIsSinglePassAndPreservesLiteralOrMalformedEscapes(string raw, string expected)
        => Assert.Equal(expected, CadUnicodeText.Decode(raw));

    private static string Encode(string text) => string.Concat(text.Select(c => c > 127 ? @"\U+" + ((int)c).ToString("X4") : c.ToString()));
    private sealed class UnusedClient : IDeepSeekClient
    {
        public Task<string> ChatCompletionAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
            => throw new InvalidOperationException("No network calls in formatting regression.");
    }
}
