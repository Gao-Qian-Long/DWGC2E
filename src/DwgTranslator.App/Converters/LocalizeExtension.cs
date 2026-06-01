using System.Windows.Data;
using System.Windows.Markup;
using DwgTranslator.Core.Resources;

namespace DwgTranslator.App.Converters;

/// <summary>
/// XAML markup extension for localized string lookup.
/// Usage: {local:Localize KeyName} or {local:Localize KeyName, Arg1, Arg2}
/// Supports runtime language switching via Binding to LocalizationService.LanguageChanged.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class LocalizeExtension : MarkupExtension
{
    public string Key { get; }

    public object[] Args { get; }

    public LocalizeExtension(string key) : this(key, Array.Empty<object>()) { }

    public LocalizeExtension(string key, object arg0) : this(key, new[] { arg0 }) { }

    public LocalizeExtension(string key, object arg0, object arg1) : this(key, new[] { arg0, arg1 }) { }

    public LocalizeExtension(string key, object[] args)
    {
        Key = key;
        Args = args;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (Args == null || Args.Length == 0)
            return Strings.Get(Key);

        // Convert all args to object[] for string.Format
        var stringArgs = Args.Select(a => a?.ToString() ?? "").ToArray();
        return Strings.Get(Key, stringArgs);
    }
}
