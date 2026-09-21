using System.Text.RegularExpressions;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Translation;

/// <summary>Restores CAD formatting codes around translated plain text without requiring an AI client.</summary>
public interface IFormatCodeRestorer
{
    string Restore(string translated, string rawText);
}

public sealed class FormatCodeRestorer : IFormatCodeRestorer
{
    private readonly IFormatCodeParser _formatCodeParser;

    public FormatCodeRestorer(IFormatCodeParser formatCodeParser)
    {
        _formatCodeParser = formatCodeParser ?? throw new ArgumentNullException(nameof(formatCodeParser));
    }

    public string Restore(string translated, string rawText)
    {
        if (string.IsNullOrEmpty(rawText) || string.IsNullOrEmpty(translated)) return translated;

        var (_, template, codes) = _formatCodeParser.Parse(rawText);
        if (codes.Count == 0) return translated;

        var parts = Regex.Split(template, @"(__FMT_\d+__)");
        var textSegmentIndexes = new List<int>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (!Regex.IsMatch(parts[i], @"^__FMT_\d+__$") && !string.IsNullOrEmpty(parts[i]))
                textSegmentIndexes.Add(i);
        }

        if (textSegmentIndexes.Count == 0)
        {
            var pure = string.Concat(parts) + translated;
            var pureResult = _formatCodeParser.Restore(pure, codes);
            return PreserveMTextParagraphs(rawText, pureResult);
        }

        if (textSegmentIndexes.Count == 1)
        {
            parts[textSegmentIndexes[0]] = translated;
        }
        else
        {
            var originalSegments = textSegmentIndexes.Select(i => parts[i]).ToList();
            var translatedLines = translated.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            if (translatedLines.Length == originalSegments.Count)
            {
                for (var i = 0; i < textSegmentIndexes.Count; i++)
                    parts[textSegmentIndexes[i]] = translatedLines[i];
            }
            else if (originalSegments.Count == 2
                     && !TranslationQualityValidator.ContainsCjk(originalSegments[0])
                     && !string.IsNullOrWhiteSpace(originalSegments[0])
                     && translated.TrimStart().StartsWith(originalSegments[0].Trim(), StringComparison.Ordinal))
            {
                var prefix = originalSegments[0].Trim();
                parts[textSegmentIndexes[0]] = prefix;
                parts[textSegmentIndexes[1]] = translated.TrimStart().Substring(prefix.Length).TrimStart();
            }
            else
            {
                // Line counts don't line up with the template segments. Put the full
                // translation into the longest original segment and KEEP the other
                // segments' original text — clearing them dropped fixed labels
                // (units, tags) that live between format codes.
                var longestIndex = 0;
                var longestLength = 0;
                for (var i = 0; i < originalSegments.Count; i++)
                {
                    if (originalSegments[i].Length <= longestLength) continue;
                    longestLength = originalSegments[i].Length;
                    longestIndex = i;
                }

                for (var i = 0; i < textSegmentIndexes.Count; i++)
                    parts[textSegmentIndexes[i]] = i == longestIndex ? translated : originalSegments[i];
            }
        }

        var result = _formatCodeParser.Restore(string.Concat(parts), codes);
        return PreserveMTextParagraphs(rawText, result);
    }

    private static string PreserveMTextParagraphs(string rawText, string value) =>
        rawText.Contains("\\P", StringComparison.Ordinal)
            ? value.Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P")
            : value;
}
