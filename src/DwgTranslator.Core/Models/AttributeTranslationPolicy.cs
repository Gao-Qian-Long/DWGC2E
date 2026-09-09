namespace DwgTranslator.Core.Models;

/// <summary>Electrical title-block field mappings are machine data, not annotations.</summary>
public static class AttributeTranslationPolicy
{
    public static bool IsMetadataTag(string tag) =>
        string.Equals(tag, "WD_TB", StringComparison.OrdinalIgnoreCase);

    public static bool IsMetadataHandle(string handle)
    {
        int separator = handle.LastIndexOf('/');
        return separator >= 0 && IsMetadataTag(handle.Substring(separator + 1));
    }
}
