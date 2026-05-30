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
    /// </summary>
    public static double EstimateTextWidth(string text, double height)
    {
        if (string.IsNullOrEmpty(text) || height <= 0) return 0;
        double width = 0;
        foreach (char c in text)
        {
            if (c == ' ') width += height * 0.20;
            else if (c >= 0x4E00 && c <= 0x9FFF) width += height * 1.0;      // CJK Unified Ideographs
            else if (c >= 0x3000 && c <= 0x303F) width += height * 1.0;      // CJK Symbols and Punctuation
            else if (c >= 0xFF00 && c <= 0xFFEF) width += height * 1.0;      // Fullwidth forms
            else if (c >= 0x3040 && c <= 0x309F) width += height * 1.0;      // Hiragana
            else if (c >= 0x30A0 && c <= 0x30FF) width += height * 1.0;      // Katakana
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
}
