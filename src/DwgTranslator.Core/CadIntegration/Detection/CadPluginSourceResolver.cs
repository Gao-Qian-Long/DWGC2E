namespace DwgTranslator.Core.Services;

/// <summary>Shared installed plugin preference; does not load DLLs or probe development outputs.</summary>
public static class CadPluginSourceResolver
{
    public static string? ResolveExisting(string applicationDirectory, string? configuredPath)
    {
        // A persisted path must not override the plugin shipped with this APP.
        foreach (var path in new[]
        {
            Path.Combine(applicationDirectory, "CadPlugin", CadPluginInstaller.PluginFileName),
            Path.Combine(applicationDirectory, CadPluginInstaller.PluginFileName),
            configuredPath
        })
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
        }
        return null;
    }
}
