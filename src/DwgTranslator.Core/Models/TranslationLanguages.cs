namespace DwgTranslator.Core.Models;

/// <summary>
/// A language the translator can read from or write to.
/// </summary>
/// <param name="Code">BCP-47-ish code used in settings and the CAD writeback config, e.g. "ZH", "JA".</param>
/// <param name="EnglishName">Name handed to the model in the translation prompt, e.g. "Japanese".</param>
/// <param name="NativeName">Name shown next to the English one in the UI, e.g. "日本語".</param>
/// <param name="IsCjk">True when the language is written with Han/Kana/Hangul characters.</param>
public sealed record TranslationLanguage(string Code, string EnglishName, string NativeName, bool IsCjk)
{
    /// <summary>Label used by the language pickers: "Japanese · 日本語".</summary>
    public string DisplayName => NativeName == EnglishName ? EnglishName : $"{NativeName} · {EnglishName}";

    /// <summary>
    /// Readable name for anything that falls back to ToString() — accessibility names, logs and
    /// any control template that cannot apply DisplayMemberPath. Without this the record prints its
    /// whole property dump (which is how a combo box once showed "TranslationLanguage { Code = ZH … }").
    /// </summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// The languages the translator offers, and the script facts the rest of the pipeline needs.
///
/// The pipeline used to carry a single <c>targetIsCjk</c> boolean, which made "Chinese to English" and
/// "English to Chinese" the only expressible directions: every font decision, quality check and
/// filter rule compared the codes against the literal strings "ZH"/"EN". The facts the code
/// actually needs are (a) which language is being written, so the CAD-side font mapping can pick a
/// CJK or a Latin face, and (b) which scripts are present, so an untranslated echo can be spotted.
/// Both are derived here from a code instead.
/// </summary>
public static class TranslationLanguages
{
    public static readonly IReadOnlyList<TranslationLanguage> All =
    [
        new("ZH", "Chinese (Simplified)", "简体中文", true),
        new("ZH-TW", "Chinese (Traditional)", "繁體中文", true),
        new("EN", "English", "English", false),
        new("JA", "Japanese", "日本語", true),
        new("KO", "Korean", "한국어", true),
        new("RU", "Russian", "Русский", false),
        new("DE", "German", "Deutsch", false),
        new("FR", "French", "Français", false),
        new("ES", "Spanish", "Español", false),
        new("PT", "Portuguese", "Português", false),
        new("IT", "Italian", "Italiano", false),
        new("NL", "Dutch", "Nederlands", false),
        new("PL", "Polish", "Polski", false),
        new("TR", "Turkish", "Türkçe", false),
        new("VI", "Vietnamese", "Tiếng Việt", false),
        new("TH", "Thai", "ไทย", false),
        new("ID", "Indonesian", "Bahasa Indonesia", false),
        new("AR", "Arabic", "العربية", false)
    ];

    public static TranslationLanguage? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = Normalize(code);
        return All.FirstOrDefault(l => string.Equals(l.Code, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Maps legacy/short spellings ("zh-cn", "cn", "chs") onto a catalog code.</summary>
    public static string Normalize(string? code)
    {
        var value = (code ?? string.Empty).Trim().ToUpperInvariant();
        return value switch
        {
            "" => "ZH",
            "CN" or "CHS" or "ZH-CN" or "ZH-HANS" => "ZH",
            "CHT" or "ZH-HANT" or "ZH-HK" => "ZH-TW",
            "EN-US" or "EN-GB" or "ENG" => "EN",
            "JP" or "JPN" => "JA",
            "KR" or "KOR" => "KO",
            _ => value
        };
    }

    /// <summary>Name handed to the model and shown in prompts/logs, e.g. "Chinese (Simplified)".</summary>
    public static string Name(string? code) => Find(code)?.EnglishName ?? (code ?? string.Empty);

    /// <summary>Conventional bundled glossary file for one exact language direction.</summary>
    public static string GlossaryFileName(string? sourceCode, string? targetCode) =>
        $"mechanical_{Normalize(sourceCode).ToLowerInvariant().Replace('-', '_')}_{Normalize(targetCode).ToLowerInvariant().Replace('-', '_')}.json";

    /// <summary>True when the language is written with Han/Kana/Hangul characters.</summary>
    public static bool IsCjk(string? code) => Find(code)?.IsCjk ?? false;

    /// <summary>
    /// True when the language has a script of its own that a translation is expected to produce.
    /// Latin-script targets are excluded: they are the fallback script of every model, so requiring
    /// "Latin letters" would accept an untranslated English echo of a Japanese label.
    /// </summary>
    public static bool RequiresOwnScript(string? code) => Find(code) is { IsCjk: true }
        || Normalize(code) is "RU" or "AR" or "TH";

    /// <summary>True when two languages are written with the same base script, so characters may
    /// legitimately survive translation (Chinese to Japanese keeps Han characters).</summary>
    public static bool SharesScript(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        return IsCjk(left) && IsCjk(right);
    }

    /// <summary>
    /// True when the text contains characters written in the given language's own script.
    /// Used to spot a response that still carries the source language (an untranslated echo)
    /// or that never reached the target language.
    /// </summary>
    public static bool ContainsScriptOf(string? text, string? languageCode)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return Normalize(languageCode) switch
        {
            "ZH" or "ZH-TW" => text.Any(IsHan),
            "JA" => text.Any(c => IsHan(c) || IsKana(c)),
            "KO" => text.Any(c => IsHangul(c) || IsHan(c)),
            "RU" => text.Any(IsCyrillic),
            "AR" => text.Any(IsArabic),
            "TH" => text.Any(c => c >= '\u0E00' && c <= '\u0E7F'),
            _ => false
        };
    }

    public static bool ContainsHan(string? text) => text != null && text.Any(IsHan);

    private static bool IsHan(char c) =>
        (c >= '\u3400' && c <= '\u4DBF') ||
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\uF900' && c <= '\uFAFF');

    private static bool IsKana(char c) =>
        (c >= '\u3040' && c <= '\u309F') ||   // Hiragana
        (c >= '\u30A0' && c <= '\u30FF');     // Katakana

    private static bool IsHangul(char c) =>
        (c >= '\uAC00' && c <= '\uD7AF') ||   // syllables
        (c >= '\u1100' && c <= '\u11FF');     // jamo

    private static bool IsCyrillic(char c) => c >= '\u0400' && c <= '\u04FF';
    private static bool IsArabic(char c) => c >= '\u0600' && c <= '\u06FF';
}
