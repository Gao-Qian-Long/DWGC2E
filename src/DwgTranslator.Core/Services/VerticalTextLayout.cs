using System.Text.RegularExpressions;

namespace DwgTranslator.Core.Services;

/// <summary>Detect a CJK character column, not an ordinary multi-line paragraph.</summary>
public static class VerticalTextLayout
{
    public static bool IsCharacterColumn(string plainText, double rotation, double width, double height)
    {
        if (height <= 0 || Math.Abs(Math.Sin(rotation)) > .001) return false;
        if (Regex.Matches(plainText ?? "", @"[\u3400-\u9fff]").Count < 2) return false;
        if (width > 0 && width <= height * 1.35) return true;
        var lines = Regex.Split((plainText ?? "").Trim(), @"\r\n|\r|\n|\\P");
        return lines.Length >= 2 && lines.All(line => Regex.IsMatch(line.Trim(), @"^[\u3400-\u9fff]$"));
    }

    /// <summary>
    /// Preserve the source character-column intent in the target language.
    /// Latin translations read as one coherent line after the CAD entity is rotated 90 degrees;
    /// CJK translations remain upright and receive a hard paragraph break between ideographs.
    /// A leading equipment identifier such as 35A is kept together on its own row.
    /// </summary>
    public static string FormatTranslatedColumn(string translatedText, bool targetIsCjk)
    {
        var normalized = Regex.Replace(
            (translatedText ?? "").Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P"),
            @"(?:\\P)+", "\\P").Trim();

        if (!targetIsCjk)
            return Regex.Replace(normalized.Replace("\\P", " "), @"\s+", " ").Trim();

        // Put an identifier before the first ideograph on a separate row, then stack every
        // contiguous ideograph. Formatting wrappers remain intact because breaks are inserted
        // only at visible Latin/CJK and CJK/CJK boundaries.
        normalized = Regex.Replace(normalized, @"(?<=[A-Za-z0-9])(?=[\u3400-\u9fff])", "\\P");
        return Regex.Replace(normalized, @"(?<=[\u3400-\u9fff])(?=[\u3400-\u9fff])", "\\P");
    }

    public static double TargetRotation(double sourceRotation, bool targetIsCjk) =>
        targetIsCjk ? sourceRotation : Math.PI / 2;
}
