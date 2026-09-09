#if GSTARCAD
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.Geometry;
using Gssoft.Gscad.Runtime;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#endif

[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.HealthCheckCommand))]

namespace DwgTranslator.Cad.Commands;

public sealed class HealthCheckCommand
{
    [CommandMethod("DWGTRANSLATORPING")]
    public void Execute()
    {
        var markerDir = Path.Combine(Path.GetTempPath(), "DwgTranslator");
        Directory.CreateDirectory(markerDir);
        var marker = Path.Combine(markerDir, "plugin_ping.txt");
        var runtime = typeof(HealthCheckCommand).Assembly.ImageRuntimeVersion;
#if GSTARCAD
        const string platform = "GstarCAD";
#else
        const string platform = "AutoCAD";
#endif
        var result = $"status=ok|platform={platform}|runtime={runtime}|utc={DateTime.UtcNow:O}";
        File.WriteAllText(marker, result);
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            $"\n[DwgTranslator] Plugin OK ({platform}, {runtime})\n");
    }

    [CommandMethod("DWGTRANSLATORCREATETEST")]
    public void CreateTestDrawing()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var testDir = Path.Combine(Path.GetTempPath(), "DwgTranslator");
        Directory.CreateDirectory(testDir);
        var drawingPath = Path.Combine(testDir, "online_writeback_source.dwg");
        var markerPath = Path.Combine(testDir, "online_writeback_source.txt");

        string handle;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(
                blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            using var text = new DBText
            {
                Position = new Point3d(0, 0, 0),
                Height = 2.5,
                TextString = "Hello Motor"
            };
            modelSpace.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
            handle = text.Handle.ToString();
            tr.Commit();
        }

        doc.Database.SaveAs(drawingPath, DwgVersion.Current);
        File.WriteAllText(markerPath, $"path={drawingPath}|handle={handle}");
        doc.Editor.WriteMessage($"\n[DwgTranslator] Test drawing created: {drawingPath}\n");
    }
}
