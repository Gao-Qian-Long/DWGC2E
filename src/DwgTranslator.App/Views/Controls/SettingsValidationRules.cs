using System.Globalization;
using System.IO;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

public sealed class IntegerRangeValidationRule : ValidationRule
{
    public int Minimum { get; set; }
    public int Maximum { get; set; }
    public string FieldName { get; set; } = "数值";

    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        var text = value?.ToString()?.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, cultureInfo, out var number))
            return new ValidationResult(false, $"{FieldName}必须是整数。");
        return number < Minimum || number > Maximum
            ? new ValidationResult(false, $"{FieldName}范围为 {Minimum}–{Maximum}。")
            : ValidationResult.ValidResult;
    }
}

public sealed class RequiredTextValidationRule : ValidationRule
{
    public string FieldName { get; set; } = "此项";

    public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
        string.IsNullOrWhiteSpace(value?.ToString())
            ? new ValidationResult(false, $"{FieldName}不能为空。")
            : ValidationResult.ValidResult;
}

public sealed class AbsoluteDirectoryValidationRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        var path = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return new ValidationResult(false, "请选择输出目录。");
        try
        {
            return Path.IsPathFullyQualified(path)
                ? ValidationResult.ValidResult
                : new ValidationResult(false, "请选择完整的绝对目录路径。");
        }
        catch
        {
            return new ValidationResult(false, "目录路径格式无效。");
        }
    }
}

public sealed class OutputNamingPatternValidationRule : ValidationRule
{
    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        var pattern = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
            return new ValidationResult(false, "命名规则不能为空。");
        if (pattern.IndexOfAny(InvalidCharacters) >= 0)
            return new ValidationResult(false, "命名规则不能包含路径分隔符或文件名非法字符。");

        var reduced = pattern
            .Replace("{name}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{lang}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (reduced.Contains('{') || reduced.Contains('}'))
            return new ValidationResult(false, "仅支持 {name}、{lang}、{date} 三个变量。");

        return ValidationResult.ValidResult;
    }
}

