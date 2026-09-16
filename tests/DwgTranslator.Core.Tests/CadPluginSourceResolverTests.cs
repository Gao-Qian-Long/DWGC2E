using DwgTranslator.Core.Services;
namespace DwgTranslator.Core.Tests;

public sealed class CadPluginSourceResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-plugin-source-" + Guid.NewGuid().ToString("N"));
    // Text fixtures only: these files are never loaded as assemblies.
    private string Fixture(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "inert path selection fixture");
        return path;
    }
    [Theory]
    [InlineData(true, true, true, "bundled")]
    [InlineData(false, true, true, "flat")]
    [InlineData(false, false, true, "custom")]
    [InlineData(false, false, false, "none")]
    [InlineData(true, false, false, "bundled")]
    public void UsesSameInstalledPriority(bool bundled, bool flat, bool custom, string expected)
    {
        var app = Path.Combine(root, "app");
        string? bundlePath = bundled ? Fixture("app/CadPlugin/DwgTranslator.Cad.dll") : null;
        string? flatPath = flat ? Fixture("app/DwgTranslator.Cad.dll") : null;
        var customPath = custom ? Fixture("custom/DwgTranslator.Cad.dll") : Path.Combine(root, "missing.dll");
        var actual = CadPluginSourceResolver.ResolveExisting(app, customPath);
        Assert.Equal(expected switch { "bundled" => bundlePath, "flat" => flatPath, "custom" => customPath, _ => null }, actual);
        if (custom) Assert.Equal("inert path selection fixture", File.ReadAllText(customPath));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingSourcesReturnNullWithoutCreatingFiles(string? configured)
    {
        Assert.Null(CadPluginSourceResolver.ResolveExisting(root, configured));
        Assert.False(Directory.Exists(root));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
