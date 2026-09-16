using DwgTranslator.Core.Services;
using System.Text.Json;

namespace DwgTranslator.Core.Tests;

public sealed class CadPluginPayloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-payload-" + Guid.NewGuid().ToString("N"));
    private string Cad => Path.Combine(root, "cad");
    private string Source => Path.Combine(root, "package");
    private string Installed => CadPluginInstaller.ResolvePluginDirectory(Cad);
    private static readonly string[] Required = ["DwgTranslator.Cad.dll", "DwgTranslator.Core.dll", "cad-platform.txt"];

    public CadPluginPayloadTests()
    {
        Directory.CreateDirectory(Path.Combine(Cad, "Support"));
        File.WriteAllText(Path.Combine(Cad, "gcad.exe"), "fixture-not-executed");
        foreach (var name in Required) Put(Source, name, name == "cad-platform.txt" ? "GstarCAD" : "fixture-not-loaded");
    }

    private static void Put(string directory, string name, string text)
    {
        var file = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
    private void Manifest(params string[] extras) => File.WriteAllLines(Path.Combine(Source, "cad-files.txt"), [.. Required, .. extras]);
    private void Install() => Assert.True(CadPluginInstaller.Install(Cad, Source).Success);

    [Fact]
    public void ExplicitDependenciesAreInstalledCheckedRepairedAndUninstalled()
    {
        Put(Source, "Private.dll", "version1");
        Put(Source, "zh/Private.resources.dll", "satellite");
        Put(Source, "unlisted.txt", "do-not-copy");
        Manifest("Private.dll", "zh/Private.resources.dll");
        Install();
        Assert.True(CadPluginInstaller.Inspect(Cad, Source).Ready);
        Assert.Equal("satellite", File.ReadAllText(Path.Combine(Installed, "zh/Private.resources.dll")));
        Assert.False(File.Exists(Path.Combine(Installed, "unlisted.txt")));
        Put(Installed, "Private.dll", "damaged");
        Assert.False(CadPluginInstaller.Inspect(Cad, Source).Ready);
        Install();
        Assert.Equal("version1", File.ReadAllText(Path.Combine(Installed, "Private.dll")));
        File.Delete(Path.Combine(Installed, "zh/Private.resources.dll"));
        Assert.False(CadPluginInstaller.Inspect(Cad, Source).Ready);
        Install();
        Put(Installed, "user-note.txt", "preserve");
        Assert.True(CadPluginInstaller.Uninstall(Cad).Success);
        Assert.False(File.Exists(Path.Combine(Installed, "Private.dll")));
        Assert.False(File.Exists(Path.Combine(Installed, "zh/Private.resources.dll")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Installed, "user-note.txt")));
    }

    [Fact]
    public void UninstallPreservesModifiedOwnedFiles()
    {
        Manifest(); Install();
        Put(Installed, "DwgTranslator.Cad.dll", "user-modification");
        Assert.True(CadPluginInstaller.Uninstall(Cad).Success);
        Assert.Equal("user-modification", File.ReadAllText(Path.Combine(Installed, "DwgTranslator.Cad.dll")));
        Assert.True(CadPluginInstaller.Uninstall(Cad).Success);
        Assert.Equal("user-modification", File.ReadAllText(Path.Combine(Installed, "DwgTranslator.Cad.dll")));
    }

    [Fact]
    public void OldThreeFilePackageRemainsCompatible()
    {
        Install();
        Assert.True(CadPluginInstaller.Inspect(Cad, Source).Ready);
        Assert.True(CadPluginInstaller.Uninstall(Cad).Success);
        Assert.False(File.Exists(Path.Combine(Installed, "DwgTranslator.Cad.dll")));
    }

    [Fact]
    public void MissingDeclaredDependencyFailsBeforeTouchingInstallation()
    {
        Manifest("missing.dll");
        Assert.False(CadPluginInstaller.Install(Cad, Source).Success);
        Assert.False(Directory.Exists(Installed));
        Assert.False(File.Exists(CadPluginInstaller.ResolveAutoLoadPath(Cad)));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("nested/../../escape.dll")]
    [InlineData("C:/escape.dll")]
    [InlineData("nested//extra.dll")]
    [InlineData("extra.dll:stream")]
    [InlineData("extra.dll.")]
    [InlineData("DwgTranslator.Cad.dll")]
    [InlineData("dwgtranslator.cad.DLL")]
    [InlineData("cad-files.txt")]
    [InlineData("AcDbMgd.dll")]
    [InlineData("dwgc2e-installed-files.json")]
    [InlineData("nested/GcMgd.dll")]
    public void InvalidManifestIsRejectedWithoutWriting(string extra)
    {
        Manifest(extra);
        Assert.False(CadPluginInstaller.Install(Cad, Source).Success);
        Assert.False(CadPluginInstaller.Inspect(Cad, Source).Ready);
        Assert.False(Directory.Exists(Installed));
    }

    [Fact]
    public void UnknownDestinationDependencyIsNeverOverwritten()
    {
        Manifest("Private.dll");
        Put(Source, "Private.dll", "package");
        Put(Installed, "Private.dll", "user");
        Assert.False(CadPluginInstaller.Install(Cad, Source).Success);
        Assert.Equal("user", File.ReadAllText(Path.Combine(Installed, "Private.dll")));
        Assert.False(File.Exists(Path.Combine(Installed, "DwgTranslator.Cad.dll")));
    }

    [Fact]
    public void UpgradeKeepsOwnershipForRemovedDependenciesWithoutDeletingDuringInstall()
    {
        Manifest("Old.dll"); Put(Source, "Old.dll", "owned"); Install();
        Manifest(); Install();
        Assert.True(File.Exists(Path.Combine(Installed, "Old.dll")));
        Assert.True(CadPluginInstaller.Uninstall(Cad).Success);
        Assert.False(File.Exists(Path.Combine(Installed, "Old.dll")));
    }

    [Fact]
    public void InvalidReceiptCannotRemoveAutoloadOrEscapeDirectory()
    {
        Install();
        var startup = CadPluginInstaller.ResolveAutoLoadPath(Cad);
        var before = File.ReadAllText(startup);
        Put(Installed, "dwgc2e-installed-files.json", JsonSerializer.Serialize(new Dictionary<string, string>
        { ["../outside.dll"] = new string('A', 64) }));
        Assert.False(CadPluginInstaller.Uninstall(Cad).Success);
        Assert.Equal(before, File.ReadAllText(startup));
        Assert.True(File.Exists(Path.Combine(Installed, "DwgTranslator.Cad.dll")));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("dwgc2e-payload-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture path");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
