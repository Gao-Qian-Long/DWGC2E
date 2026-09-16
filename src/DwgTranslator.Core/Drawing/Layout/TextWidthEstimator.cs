namespace DwgTranslator.Core.Services;

/// <summary>
/// Shared text width estimation utility used by both online (AutoCAD) and offline (ACadSharp) write engines.
/// Provides consistent per-character width ratios for CJK and Latin text.
/// </summary>
public static class TextWidthEstimator
{
    /// <summary>
    /// Estimates the rendered width of a text string based on character types.
    /// Uses heuristic width ratios: CJK characters are ~1.0× height, Latin ~0.45-0.55× height.
    ///
    /// NOTE: These ratios are averages for common TrueType fonts (Arial, SimHei).
    /// For SHX monospace fonts (simplex.shx, romans.shx), all characters are ~0.58× height
    /// — the estimator under-estimates SHX by up to 29%. When the text style is known
    /// to be SHX, callers should adjust accordingly.
    /// </summary>
    public static double EstimateTextWidth(string text, double height)
    {
        if (string.IsNullOrEmpty(text) || height <= 0) return 0;
        double width = 0;
        foreach (char c in text)
        {
            if (c == ' ') width += height * 0.20;
            else if (c >= 0x4E00 && c <= 0x9FFF) width += height * 1.0;      // CJK Unified Ideographs
            else if (c >= 0x3000 && c <= 0x303F)
            {
                // CJK Symbols and Punctuation: distinguish full-width symbols from
                // half-width punctuation (、。〃 etc. are typically ~0.5× height).
                if (c <= 0x3003 || (c >= 0x3008 && c <= 0x3011) || c == 0x301C || c == 0x3030)
                    width += height * 0.55;  // Half-width CJK punctuation
                else
                    width += height * 0.80;  // CJK symbols: moderate width
            }
            else if (c >= 0xFF00 && c <= 0xFFEF) width += height * 1.0;      // Fullwidth forms
            else if (c >= 0x3040 && c <= 0x309F) width += height * 0.85;     // Hiragana (not full 1.0)
            else if (c >= 0x30A0 && c <= 0x30FF) width += height * 0.85;     // Katakana (not full 1.0)
            else if (char.IsUpper(c)) width += height * 0.55;
            else if (char.IsLower(c)) width += height * 0.45;
            else if (char.IsDigit(c)) width += height * 0.50;
            else width += height * 0.45;                                      // punctuation, symbols
        }
        return width;
    }

    /// <summary>
    /// Estimates the number of lines a text will occupy given a rectangle width.
    /// Handles both hard line breaks (\P, \n) and soft wrapping.
    /// </summary>
    public static int EstimateLineCount(string text, double height, double rectWidth)
    {
        if (rectWidth <= 0 || string.IsNullOrEmpty(text) || height <= 0) return 1;
        var hardLines = text.Split(new[] { "\\P", "\n", "\r\n" }, StringSplitOptions.None);
        int totalLines = 0;
        foreach (var line in hardLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                totalLines++;
                continue;
            }
            double lineWidth = EstimateTextWidth(line, height);
            totalLines += Math.Max(1, (int)Math.Ceiling(lineWidth / rectWidth));
        }
        return Math.Max(1, totalLines);
    }

    /// <summary>
    /// Rebuilds text content with \P line breaks to approximate a target line count,
    /// using character-width-aware greedy distribution for balanced line widths.
    /// Splits on word boundaries for Latin text and individual CJK characters.
    /// Used by both online (AcadWriterEngine, TextReplacer, LayoutOptimizer) and
    /// offline (DwgWriterService) writeback paths.
    /// </summary>
    public static string RebuildMTextWithLineBreaks(string text, int targetLineCount, double textHeight = 1.0)
    {
        if (targetLineCount <= 1 || string.IsNullOrEmpty(text))
            return text;

        var segments = SegmentText(text);

        double totalWidth = 0;
        foreach (var seg in segments)
            totalWidth += EstimateTextWidth(seg, textHeight);

        double targetWidthPerLine = totalWidth / targetLineCount;
        var lines = new List<string>();
        var lineBuilder = new System.Text.StringBuilder();
        double currentLineWidth = 0;

        foreach (var seg in segments)
        {
            double segWidth = EstimateTextWidth(seg, textHeight);
            if (currentLineWidth > 0 &&
                currentLineWidth + segWidth > targetWidthPerLine * 1.3 &&
                lines.Count < targetLineCount - 1)
            {
                lines.Add(lineBuilder.ToString().TrimEnd());
                lineBuilder.Clear();
                currentLineWidth = 0;
            }
            lineBuilder.Append(seg);
            currentLineWidth += segWidth;
        }
        if (lineBuilder.Length > 0)
            lines.Add(lineBuilder.ToString().TrimEnd());

        while (lines.Count > targetLineCount && lines.Count > 1)
        {
            int last = lines.Count - 1;
            lines[last - 1] = lines[last - 1] + " " + lines[last];
            lines.RemoveAt(last);
        }

        return string.Join("\\P", lines);
    }

    /// <summary>
    /// Re-flows text by inserting \P line breaks so each line fits within targetWidth.
    /// Used for MText that originally auto-wrapped within a fixed rectangle width
    /// (no hard \P breaks). Without this, translated English text becomes one long
    /// line that AutoCAD stretches/spreads across the rectangle.
    /// </summary>
    public static string ReflowTextToWidth(string text, double targetWidth, double textHeight)
    {
        if (string.IsNullOrEmpty(text) || targetWidth <= 0)
            return text;

        var segments = SegmentText(text);

        var lines = new List<string>();
        var lineBuilder = new System.Text.StringBuilder();
        double currentLineWidth = 0;

        foreach (var seg in segments)
        {
            double segWidth = EstimateTextWidth(seg, textHeight);

            // Start new line if adding this segment exceeds the target width
            // and we already have content on the current line
            if (currentLineWidth > 0 && currentLineWidth + segWidth > targetWidth)
            {
                lines.Add(lineBuilder.ToString().TrimEnd());
                lineBuilder.Clear();
                currentLineWidth = 0;
            }

            lineBuilder.Append(seg);
            currentLineWidth += segWidth;
        }
        if (lineBuilder.Length > 0)
            lines.Add(lineBuilder.ToString().TrimEnd());

        // If no wrapping occurred (single line fits), return original
        if (lines.Count <= 1)
            return text;

        return string.Join("\\P", lines);
    }

    /// <summary>
    /// Segments text into words (Latin) and individual characters (CJK).
    /// Shared by RebuildMTextWithLineBreaks and ReflowTextToWidth.
    /// </summary>
    private static List<string> SegmentText(string text)
    {
        var segments = new List<string>();
        var currentWord = new System.Text.StringBuilder();
        bool lastWasWord = false;
        foreach (char c in text)
        {
            if (c == ' ')
            {
                if (currentWord.Length > 0)
                {
                    segments.Add(currentWord.ToString());
                    currentWord.Clear();
                    lastWasWord = true;
                }
                // Insert space as its own segment so line rebuilders
                // correctly restore word boundaries.
                segments.Add(" ");
                lastWasWord = false;
            }
            else if (IsCjk(c))
            {
                if (currentWord.Length > 0 && !HasCjkChar(currentWord))
                {
                    segments.Add(currentWord.ToString());
                    currentWord.Clear();
                    lastWasWord = true;
                }
                if (currentWord.Length > 0)
                {
                    segments.Add(currentWord.ToString());
                    currentWord.Clear();
                }
                // Insert an implicit space before CJK when previous
                // segment was a Latin word, so CJK and Latin don't
                // run together visually.
                if (lastWasWord)
                    segments.Add(" ");
                segments.Add(c.ToString());
                lastWasWord = false;
            }
            else
            {
                currentWord.Append(c);
                lastWasWord = false;
            }
        }
        if (currentWord.Length > 0)
            segments.Add(currentWord.ToString());
        return segments;
    }

    private static bool IsCjk(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF)
            || (c >= 0x3000 && c <= 0x303F)
            || (c >= 0xFF00 && c <= 0xFFEF)
            || (c >= 0x3040 && c <= 0x309F)
            || (c >= 0x30A0 && c <= 0x30FF);
    }

    private static bool HasCjkChar(System.Text.StringBuilder sb)
    {
        if (sb.Length == 0) return false;
        return IsCjk(sb[0]);
    }
}
