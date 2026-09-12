using System.Text.Json;

namespace DwgTranslator.Core.Models;

/// <summary>
/// The single JSON contract for <see cref="AppConfig"/>.
///
/// The shipped settings.json / settings.json.example use camelCase, while <see cref="AppConfig"/>
/// is PascalCase and carries no [JsonPropertyName]. System.Text.Json is case-SENSITIVE by default,
/// so with the default options the shipped file bound nothing at all: a hand-filled API key was
/// ignored, and the next save wrote the (empty) PascalCase shape back, wiping what the user had
/// typed. Every read and write of the configuration goes through these options.
/// </summary>
public static class AppConfigJson
{
    /// <summary>Read options: tolerate either casing so camelCase and PascalCase files both work.</summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Write options use the same camelCase contract as the shipped examples. Reads remain
    /// case-insensitive so settings written by older PascalCase builds continue to work.
    /// </summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
