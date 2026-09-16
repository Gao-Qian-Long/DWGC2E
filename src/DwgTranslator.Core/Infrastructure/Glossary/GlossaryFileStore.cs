using System.Text.Json;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Infrastructure.Glossary;

/// <summary>Local editor persistence; does not validate, reload or synchronize cloud terminology.</summary>
public static class GlossaryFileStore
{
    /// <summary>Commit a local draft using the editor's existing same-directory replacement protocol.</summary>
    public static void Save(string path, IReadOnlyList<GlossaryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, AppConfigJson.WriteOptions));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}