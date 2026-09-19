using System.Globalization;
using System.Text;

namespace DwgTranslator.Core.Translation;

/// <summary>DWG Unicode escapes represent text, not formatting to preserve around a translation.</summary>
public static class CadUnicodeText
{
    public static string Decode(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            // An escaped backslash is literal; never interpret its following U as an escape.
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '\\')
            {
                result.Append(text[i]).Append(text[++i]);
                continue;
            }
            if (text[i] == '\\' && i + 6 < text.Length && text[i + 1] == 'U' && text[i + 2] == '+' &&
                ushort.TryParse(text.Substring(i + 3, 4), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var value))
            {
                result.Append((char)value);
                i += 6;
            }
            else result.Append(text[i]);
        }
        return result.ToString();
    }
}
