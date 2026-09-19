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
[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.AttributeFrameCheckCommand))]
namespace DwgTranslator.Cad.Commands;

/// <summary>
/// Opt-in, read-only diagnostic for the attribute measurement question. It prints, for the first
/// few INSERTs that carry attributes, the attribute's own position/extents next to the INSERT's
/// rendered box and the block-definition-local ATTDEF box. That decides by data whether an
/// attribute reference is stored in the coordinate system of the space that owns its INSERT (the
/// frame the obstacle snapshot uses) or in the block definition's frame, which would mean the
/// corridor measurement compares two different frames. It never writes to the drawing.
/// </summary>
public class AttributeFrameCheckCommand
{
    [CommandMethod("DWGATTRFRAME")]
    public void Execute()
    {
        var path = Application.DocumentManager.MdiActiveDocument.Editor
            .GetString("\nDrawing to inspect (read-only): ").StringResult.Trim('"');
        var report = new List<string>();
        try
        {
            using var db = new Database(false, true);
            db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, null);
            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            int inspected = 0;
            bool cap = false;
            foreach (ObjectId btrId in bt)
            {
                var owner = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (owner.IsFromExternalReference || owner.IsFromOverlayReference) continue;
                foreach (ObjectId id in owner)
                {
                    if (inspected >= 20) { cap = true; break; }
                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference insert) continue;
                    if (insert.AttributeCollection.Count == 0) continue;
                    inspected++;
                    var definition = (BlockTableRecord)tr.GetObject(insert.BlockTableRecord, OpenMode.ForRead);
                    report.Add($"INSERT={insert.Handle}|BLOCK={definition.Name}|CONTAINER={owner.Name}" +
                        $"|CONTAINER_IS_LAYOUT={owner.IsLayout}|DEFINITION_IS_ANONYMOUS={definition.IsAnonymous}");
                    report.Add($"  INSERT_POS={Format(insert.Position)}|ROTATION={insert.Rotation:F6}" +
                        $"|SCALE={insert.ScaleFactors.X:F6},{insert.ScaleFactors.Y:F6},{insert.ScaleFactors.Z:F6}");
                    report.Add($"  INSERT_EXTENTS={Extents(insert)}");
                    foreach (ObjectId attId in insert.AttributeCollection)
                    {
                        if (tr.GetObject(attId, OpenMode.ForRead) is not AttributeReference att) continue;
                        report.Add($"  ATT={insert.Handle}/{att.Tag}|INVISIBLE={att.Invisible}|ROTATION={att.Rotation:F6}" +
                            $"|HEIGHT={att.Height:F6}|POSITION={Format(att.Position)}|EXTENTS={Extents(att)}");
                        report.Add($"    POSITION_IN_BLOCK_FRAME={Format(att.Position.TransformBy(insert.BlockTransform))}");
                        foreach (ObjectId defId in definition)
                        {
                            if (tr.GetObject(defId, OpenMode.ForRead) is not AttributeDefinition attdef) continue;
                            if (!string.Equals(attdef.Tag, att.Tag, StringComparison.Ordinal)) continue;
                            report.Add($"    ATTDEF_TAG={attdef.Tag}|ATTDEF_LOCAL_POSITION={Format(attdef.Position)}" +
                                $"|ATTDEF_LOCAL_EXTENTS={Extents(attdef)}");
                        }
                        var ink = Replacement.CollisionDetector.GetCorrectedBounds(att);
                        report.Add($"    ATT_INK={Format(ink)}");
                        var allowed = Replacement.AvailableTextSpace.Measure(att, ink, tr, false, out var readingLength);
                        report.Add($"    ALLOWED={Format(allowed)}|READING_LENGTH={readingLength:F6}");
                        foreach (var issue in Replacement.AvailableTextSpace.FindIntersections(att, tr, ink))
                            report.Add("    INTERFERENCE=" + issue);
                    }
                }
                if (cap) break;
            }
            report.Add(cap ? "CAP=20 inserts inspected" : $"INSPECTED={inspected} inserts with attributes");
        }
        catch (System.Exception ex) { report.Add("ERROR=" + ex); }
        File.WriteAllLines(path + ".attrframe.report", report);
    }

    private static string Format(Point3d p) => $"({p.X:F4},{p.Y:F4},{p.Z:F4})";

    private static string Format(Extents3d box) =>
        $"({box.MinPoint.X:F4},{box.MinPoint.Y:F4})-({box.MaxPoint.X:F4},{box.MaxPoint.Y:F4})";

    private static string Extents(Entity entity)
    {
        try { return Format(entity.GeometricExtents); }
        catch (System.Exception ex) { return "N/A (" + ex.GetType().Name + ")"; }
    }
}
