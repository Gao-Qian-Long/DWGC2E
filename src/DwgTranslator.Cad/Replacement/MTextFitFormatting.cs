using System.Globalization;
using System.Text.RegularExpressions;

namespace DwgTranslator.Cad.Replacement;

/// <summary>Make inline overrides participate in measured MText fitting.</summary>
internal static class MTextFitFormatting
{
    // Consume escaped backslashes first: literal \\W and \\H are not format commands.
    private static readonly Regex Codes = new Regex(@"\\\\|\\(?<kind>[WH])(?<value>[+]?(?:\d+(?:\.\d*)?|\.\d+))(?<relative>[xX]?);", RegexOptions.CultureInvariant);

    public static string NormalizeHeights(string contents, double referenceHeight) => referenceHeight <= 0 ? contents : Codes.Replace(contents, match =>
    {
        if (match.Groups["kind"].Value != "H" || match.Groups["relative"].Value.Length != 0) return match.Value;
        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0) return match.Value;
        return "\\H" + (value / referenceHeight).ToString("0.############", CultureInfo.InvariantCulture) + "x;";
    });

    public static string ScaleWidths(string contents, double factor)
    {
        if (factor == 1) return contents;
        var scaled = Codes.Replace(contents, match =>
        {
            if (match.Groups["kind"].Value != "W" || match.Groups["relative"].Value.Length != 0) return match.Value;
            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0) return match.Value;
            return "\\W" + (value * factor).ToString("0.############", CultureInfo.InvariantCulture) + ";";
        });
        return "{\\W" + factor.ToString("0.############", CultureInfo.InvariantCulture) + ";" + scaled + "}";
    }
}
