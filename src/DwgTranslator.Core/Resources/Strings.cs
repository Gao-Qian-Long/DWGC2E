using System.Globalization;
using System.Reflection;
using System.Resources;

namespace DwgTranslator.Core.Resources;

/// <summary>
/// Strongly-typed resource accessor for localized strings.
/// Backed by Strings.resx (zh-CN default) and Strings.en-US.resx.
/// </summary>
public static class Strings
{
    private static ResourceManager? _resourceManager;
    private static CultureInfo _currentCulture = new("zh-CN");

    /// <summary>
    /// The ResourceManager instance, using the resource base name matching
    /// the embedded resource path: DwgTranslator.Core.Resources.Strings
    /// </summary>
    public static ResourceManager ResourceManager =>
        _resourceManager ??= new ResourceManager(
            "DwgTranslator.Core.Resources.Strings",
            Assembly.GetExecutingAssembly());

    /// <summary>
    /// Get or set the current UI culture for string lookups.
    /// </summary>
    public static CultureInfo CurrentCulture
    {
        get => _currentCulture;
        set
        {
            _currentCulture = value;
            CultureInfo.CurrentUICulture = value;
        }
    }

    /// <summary>
    /// Get a localized string by key.
    /// </summary>
    public static string Get(string key)
    {
        try
        {
            return ResourceManager.GetString(key, _currentCulture) ?? key;
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
            var template = ResourceManager.GetString(key, _currentCulture) ?? key;
            return string.Format(template, args);
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }
}
