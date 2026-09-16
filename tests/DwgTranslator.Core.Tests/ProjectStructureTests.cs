using System.Xml.Linq;

namespace DwgTranslator.Core.Tests;

public class ProjectStructureTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DwgTranslator.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
        }
    }

    [Fact]
    public void ExplicitCompileAndProjectReferencesResolve()
    {
        foreach (var project in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var xml = XDocument.Load(project);
            foreach (var node in xml.Descendants().Where(n => n.Name.LocalName is "Compile" or "ProjectReference" or "Content" or "Resource"))
            {
                var include = (string?)node.Attribute("Include");
                if (include == null || include.Contains('*') || include.Contains('$')) continue;
                Assert.True(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include))), $"Missing {include} in {project}");
            }
        }
    }

    [Fact]
    public void CentralIconRetainsTheExistingPackUri()
    {
        var project = XDocument.Load(Path.Combine(Root, "src/DwgTranslator.App/DwgTranslator.App.csproj"));
        Assert.Contains(project.Descendants("Resource"), n => (string?)n.Attribute("Link") == "icon.ico");
        Assert.False(File.Exists(Path.Combine(Root, "src/DwgTranslator.App/icon.ico")));
        Assert.True(File.Exists(Path.Combine(Root, "assets/icons/icon.ico")));
    }

    [Fact]
    public void CoreDoesNotReferenceDesktopUiFrameworks()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src/DwgTranslator.Core/DwgTranslator.Core.csproj"));
        Assert.DoesNotContain("PresentationFramework", project);
        Assert.DoesNotContain("<UseWPF>", project);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src/DwgTranslator.Core"), "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain("using System.Windows", File.ReadAllText(file));
    }

    [Fact]
    public void PackagingCarriesDefaultGlossaryWithoutChangingRuntimePluginPath()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src/DwgTranslator.App/DwgTranslator.App.csproj"));
        Assert.Contains("assets\\default-glossaries\\mechanical_zh_en.json", project);
        foreach (var script in new[] { "tools/New-ReleasePackage.ps1", "installer/Install.ps1" })
            Assert.Contains("'assets'", File.ReadAllText(Path.Combine(Root, script)));
        Assert.Contains("CadPlugin", project);
        Assert.True(File.Exists(Path.Combine(Root, "assets/glossaries/mechanical_zh_en.json")));
    }
}
