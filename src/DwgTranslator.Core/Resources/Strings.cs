using System.Globalization;
using System.Reflection;
using System.Resources;

namespace DwgTranslator.Core.Resources;

/// <summary>
/// Strongly-typed resource accessor for localized strings.
/// Fixed to zh-CN (Chinese Simplified) only.
/// </summary>
public static class Strings
{
    private static ResourceManager? _resourceManager;
    private static readonly CultureInfo _zhCn = new("zh-CN");

    /// <summary>
    /// The ResourceManager instance, using the resource base name matching
    /// the embedded resource path: DwgTranslator.Core.Resources.Strings
    /// </summary>
    public static ResourceManager ResourceManager =>
        _resourceManager ??= new ResourceManager(
            "DwgTranslator.Core.Resources.Strings",
            Assembly.GetExecutingAssembly());

    /// <summary>
    /// Current UI culture (fixed to zh-CN).
    /// </summary>
    public static CultureInfo CurrentCulture { get; } = _zhCn;

    /// <summary>
    /// Get a localized string by key.
    /// </summary>
    public static string Get(string key)
    {
        try
        {
            return ResourceManager.GetString(key, CurrentCulture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    /// <summary>
    /// Get a localized string by key with format arguments.
    /// </summary>
    public static string Get(string key, params object[] args)
    {
        try
        {
            var template = ResourceManager.GetString(key, CurrentCulture) ?? key;
            return string.Format(template, args);
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }
}
