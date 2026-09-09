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
}
