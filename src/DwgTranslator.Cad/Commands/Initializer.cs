#if GSTARCAD
using Gssoft.Gscad.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
#if GSTARCAD
using Gssoft.Gscad.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif
#if GSTARCAD
using Gssoft.Gscad.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif
using System.Reflection;

namespace DwgTranslator.Cad.Commands;

/// <summary>
/// AutoCAD extension application initializer.
/// Implements IExtensionApplication to ensure the assembly is properly initialized
/// when loaded via NETLOAD. This is the standard pattern for AutoCAD .NET plugins.
///
/// Also handles assembly binding conflicts (e.g. Serilog version mismatch between
/// NuGet package and AutoCAD's built-in copy) by registering AssemblyResolve.
/// </summary>
public class Initializer : IExtensionApplication
{
    /// <summary>
    /// Static constructor 鈥?runs when the type is first accessed, BEFORE Initialize().
    /// This ensures AssemblyResolve is registered early enough to resolve dependency
    /// assembly version conflicts (e.g. Serilog 4.2 vs AutoCAD's built-in 4.0).
    /// </summary>
    static Initializer()
    {
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    /// <summary>
    /// Called once when the assembly is loaded via NETLOAD.
    /// </summary>
    public void Initialize()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DwgTranslator", "logs");
        Log.InitFileLogging(logDirectory);
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc != null)
        {
            var ed = doc.Editor;
            ed.WriteMessage("\n[DwgTranslator] Plugin loaded successfully.");
        }
    }

    /// <summary>
    /// Called when the assembly is unloaded (rarely used with NETLOAD).
    /// </summary>
    public void Terminate()
    {
        Log.CloseFileLogging();
        AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
    }

    /// <summary>
    /// Resolves assembly binding conflicts by returning an already-loaded assembly
    /// with the same simple name, even if the version differs.
    /// </summary>
    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name);

        // First: exact match
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.FullName == args.Name)
                return asm;
        }

        // Second: same simple name, any version (handles Serilog 2.0 vs 4.0, etc.)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var loadedName = asm.GetName();
            if (loadedName.Name == requestedName.Name)
                return asm;
        }

        return null;
    }
}
