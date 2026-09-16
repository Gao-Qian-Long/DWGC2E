using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class RuntimeResourcePathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DWGC2E-PathTests-" + Guid.NewGuid().ToString("N"));

    private string Put(string relative, string contents)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void PluginPrefersBundledDirectoryOverLegacyLayout()
    {
        var bundled = Put("app/CadPlugin/DwgTranslator.Cad.dll", "fixture-not-loaded");
        Put("app/DwgTranslator.Cad.dll", "legacy-fixture-not-loaded");
        Assert.Equal(bundled, AutoCadDetector.FindCadPlugin(Path.Combine(root, "app")));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void PluginStillSupportsLegacySameDirectoryLayout()
    {
        var legacy = Put("app/DwgTranslator.Cad.dll", "fixture-not-loaded");
        Assert.Equal(legacy, AutoCadDetector.FindCadPlugin(Path.Combine(root, "app")));
    }

    [Fact]
    public void UserPromptTakesPrecedenceWithoutChangingBundledPrompt()
    {
        var bundled = Put("app/prompts/deepl_context.txt", "bundled");
        Put("data/prompts/deepl_context.txt", "user");
        Assert.Equal("user", TranslationPrompt.LoadSystemPrompt(Path.Combine(root, "data"), Path.Combine(root, "app")));
        Assert.Equal("bundled", File.ReadAllText(bundled));
    }

    [Fact]
    public void PromptUsesBundleWhenUserOverrideIsAbsent()
    {
        Put("app/prompts/deepl_context.txt", "bundled");
        Assert.Equal("bundled", TranslationPrompt.LoadSystemPrompt(Path.Combine(root, "data"), Path.Combine(root, "app")));
    }

    [Fact]
    public void MissingPromptsUseBuiltInFallbackWithoutCreatingFiles()
    {
        Assert.Equal(TranslationPrompt.Fallback, TranslationPrompt.LoadSystemPrompt(Path.Combine(root, "data"), Path.Combine(root, "app")));
        Assert.False(Directory.Exists(root));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("DWGC2E-PathTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove an unexpected fixture directory.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
