#if GSTARCAD
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.Runtime;
using Gssoft.Gscad.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;
#endif
[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.VerifyLayoutCommand))]
namespace DwgTranslator.Cad.Commands;
public class VerifyLayoutCommand
{
    private static Entity Resolve(Database db, Transaction tr, string handle)
    {
        var parts = handle.Split(new[] {'/'}, 2);
        var entity = (Entity)tr.GetObject(db.GetObjectId(false, new Handle(Convert.ToInt64(parts[0],16)),0),OpenMode.ForRead);
        if(parts.Length == 1) return entity;
        foreach(ObjectId id in ((BlockReference)entity).AttributeCollection)
        {
            var attribute = (AttributeReference)tr.GetObject(id,OpenMode.ForRead);
            if(string.Equals(attribute.Tag,parts[1],StringComparison.OrdinalIgnoreCase)) return attribute;
        }
        throw new InvalidOperationException("Attribute not found: " + handle);
    }
    [CommandMethod("DWGLAYOUTREGRESSION")]
    public void Regression()
    {
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;
        var directory = ed.GetString("\nRegression output directory: ").StringResult.Trim('"');
        var report = new List<string>();
        try
        {
            Directory.CreateDirectory(directory);
            var sourcePath = Path.Combine(directory, "synthetic-source.dwg");
            var outputPath = Path.Combine(directory, "synthetic-output.dwg");
            var items = new List<DwgTranslator.Core.Models.TextEntity>();
            var boxes = new Dictionary<string, Extents3d>();
            using (var db = new Database(true, true))
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    int index = 0;
                    foreach (double rotation in new[] { 0.0, Math.PI / 2, 0.6981317 })
                    foreach (bool multiline in new[] { false, true })
                    {
                        Entity entity;
                        if (multiline)
                            entity = new MText { Contents = "阀门反馈", TextHeight = 3.5, Width = 30,
                                Location = new Point3d(index * 100, 100, 0), Rotation = rotation };
                        else
                            entity = new DBText { TextString = "阀门反馈", Height = 3.5,
                                Position = new Point3d(index * 100, 0, 0), Rotation = rotation };
                        model.AppendEntity(entity);
                        tr.AddNewlyCreatedDBObject(entity, true);
                        var handle = entity.Handle.ToString();
                        boxes.Add(handle, entity.GeometricExtents);
                        items.Add(new DwgTranslator.Core.Models.TextEntity {
                            Handle = handle, RawText = "阀门反馈", PlainText = "阀门反馈",
                            TranslatedText = "Dust removal valve closed position feedback",
                            Height = 3.5, OriginalHeight = 3.5, MTextRectangleWidth = multiline ? 30 : 0,
                            Status = DwgTranslator.Core.Models.TranslationStatus.Translated });
                        index++;
                    }
                    tr.Commit();
                }
                db.SaveAs(sourcePath, DwgVersion.Current);
            }
            var result = new Replacement.AcadWriterEngine().WriteTranslations(sourcePath, outputPath, items, true);
            report.Add($"WRITEBACK={result.SuccessCount}/{items.Count}");
            report.AddRange(result.Errors);
            int passed = 0;
            if (result.SuccessCount == items.Count)
            {
                using var output = new Database(false, true);
                output.ReadDwgFile(outputPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                using var tr = output.TransactionManager.StartTransaction();
                foreach (var item in items)
                {
                    var entity = (Entity)tr.GetObject(output.GetObjectId(false,
                        new Handle(Convert.ToInt64(item.Handle, 16)), 0), OpenMode.ForRead);
                    var current = entity.GeometricExtents;
                    var old = boxes[item.Handle];
                    bool inside = current.MinPoint.X >= old.MinPoint.X - .01 && current.MinPoint.Y >= old.MinPoint.Y - .01 &&
                        current.MaxPoint.X <= old.MaxPoint.X + .01 && current.MaxPoint.Y <= old.MaxPoint.Y + .01;
                    bool content = entity is DBText text ? text.TextString == item.TranslatedText :
                        entity is MText mt && mt.Text == item.TranslatedText;
                    if (inside && content) passed++;
                    report.Add($"HANDLE={item.Handle}|TYPE={entity.GetType().Name}|INSIDE={inside}|CONTENT={content}");
                }
            }
            report.Add($"SYNTHETIC_PASSED={passed}/{items.Count}");
        }
        catch (System.Exception ex) { report.Add("REGRESSION_ERROR=" + ex); }
        File.WriteAllLines(Path.Combine(directory, "synthetic.report"), report);
    }

    [CommandMethod("DWGVERIFYLAYOUT")]
    public void Execute()
    {
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;
        var input = ed.GetString("\nAudit request file: ").StringResult.Trim('"');
        try
        {
            var lines = File.ReadAllLines(input);
            using var source = new Database(false, true);
            using var output = new Database(false, true);
            source.ReadDwgFile(lines[0], FileOpenMode.OpenForReadAndAllShare, false, null);
            output.ReadDwgFile(lines[1], FileOpenMode.OpenForReadAndAllShare, false, null);
            using var a = source.TransactionManager.StartTransaction();
            using var b = output.TransactionManager.StartTransaction();
            var report = new List<string>();
            int passed = 0;
            var handles = lines[2].Split(',');
            var samples = new List<(string Handle, ObjectId Owner, Extents3d Old, Extents3d New)>();
            foreach (var handle in handles)
            {
                var oldEntity = Resolve(source,a,handle);
                var newEntity = Resolve(output,b,handle);
                var oldBox = Replacement.CollisionDetector.GetCorrectedBounds(oldEntity);
                var newBox = Replacement.CollisionDetector.GetCorrectedBounds(newEntity);
                samples.Add((handle,oldEntity.OwnerId,oldBox,newBox));
                bool column=oldEntity is MText sourceText && DwgTranslator.Core.Services.VerticalTextLayout.IsCharacterColumn(
                    sourceText.Text,sourceText.Rotation,sourceText.Width,sourceText.TextHeight);
                var allowedBox = Replacement.AvailableTextSpace.Measure(oldEntity, oldBox, a,column);
                const double epsilon = 0.01;
                bool fits = newBox.MinPoint.X >= allowedBox.MinPoint.X - epsilon &&
                    newBox.MaxPoint.X <= allowedBox.MaxPoint.X + epsilon &&
                    newBox.MinPoint.Y >= allowedBox.MinPoint.Y - epsilon &&
                    newBox.MaxPoint.Y <= allowedBox.MaxPoint.Y + epsilon;
                if (fits) passed++;
                double oldHeight = oldEntity is DBText ot ? ot.Height : oldEntity is MText om ? om.TextHeight : 0;
                double newHeight = newEntity is DBText nt ? nt.Height : newEntity is MText nm ? nm.TextHeight : 0;
                double oldWidthFactor = oldEntity is DBText ow ? ow.WidthFactor : 1;
                double newWidthFactor = newEntity is DBText nw ? nw.WidthFactor : 1;
                report.Add($"HANDLE={handle}|INSIDE={fits}|OLD_HEIGHT={oldHeight}|NEW_HEIGHT={newHeight}|OLD_WIDTH_FACTOR={oldWidthFactor}|NEW_WIDTH_FACTOR={newWidthFactor}|OLD={oldBox}|NEW={newBox}");
                if(column)report.Add($"COLUMN={handle}|ROTATION={(newEntity as MText)?.Rotation}|SINGLE_LINE={(newEntity as MText)?.Width==0}");
                foreach(var issue in Replacement.AvailableTextSpace.FindIntersections(newEntity,b))
                    report.Add("INTERFERENCE_CANDIDATE="+issue);
            }
            report.Add($"ENVELOPES_PASSED={passed}/{handles.Length}");
            bool Overlap(Extents3d x,Extents3d y) =>
                Math.Min(x.MaxPoint.X,y.MaxPoint.X)-Math.Max(x.MinPoint.X,y.MinPoint.X) > .01 &&
                Math.Min(x.MaxPoint.Y,y.MaxPoint.Y)-Math.Max(x.MinPoint.Y,y.MinPoint.Y) > .01;
            int newOverlaps=0, allOverlaps=0;
            for(int i=0;i<samples.Count;i++)
            for(int j=i+1;j<samples.Count;j++)
                if(samples[i].Owner == samples[j].Owner && Overlap(samples[i].New,samples[j].New))
                {
                    allOverlaps++;
                    bool existed=Overlap(samples[i].Old,samples[j].Old);
                    if(!existed)newOverlaps++;
                    report.Add($"TEXT_OVERLAP={samples[i].Handle},{samples[j].Handle}|EXISTED={existed}");
                }
            report.Add($"NEW_TEXT_OVERLAPS={newOverlaps}");
            report.Add($"ALL_TEXT_OVERLAPS={allOverlaps}");
            File.WriteAllLines(input + ".report", report);
        }
        catch (System.Exception ex) { File.WriteAllText(input + ".report", "AUDIT_ERROR=" + ex); }
    }
}
