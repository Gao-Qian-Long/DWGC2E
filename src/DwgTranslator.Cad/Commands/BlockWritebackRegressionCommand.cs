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
using DwgTranslator.Cad.Replacement;
using TextEntity = DwgTranslator.Core.Models.TextEntity;
using TranslationStatus = DwgTranslator.Core.Models.TranslationStatus;
[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.BlockWritebackRegressionCommand))]
namespace DwgTranslator.Cad.Commands;

/// <summary>Opt-in synthetic block coverage; never opens a user drawing.</summary>
public class BlockWritebackRegressionCommand
{
    [CommandMethod("DWGBLOCKREGRESSION")]
    public void Run()
    {
        var directory = Application.DocumentManager.MdiActiveDocument.Editor
            .GetString("\nNew regression directory: ").StringResult.Trim('"');
        var report = new List<string>();
        try
        {
            if (Directory.Exists(directory)) throw new InvalidOperationException("Use a new directory.");
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "source.dwg");
            var output = Path.Combine(directory, "output.dwg");
            var items = new List<TextEntity>();
            var expected = new Dictionary<string,string>();
            using (var db = new Database(true, true))
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                    var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var styles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite);
                    var style = new TextStyleTableRecord { Name = "BlockRegression", FileName = "simplex.shx", BigFontFileName = "gbcbig.shx" };
                    styles.Add(style); tr.AddNewlyCreatedDBObject(style, true);
                    var parent = new BlockTableRecord { Name = "ProcessCard" };
                    bt.Add(parent); tr.AddNewlyCreatedDBObject(parent, true);
                    foreach (var name in new[] { "Operations", "*U", "NarrowProcessCard", "TitleCell" })
                    {
                        bool narrow = name == "NarrowProcessCard", title = name == "TitleCell";
                        double cellWidth = title ? 1000 : narrow ? 14 : 65, rowHeight = title ? 280 : narrow ? 8 : 20, height = title ? 200 : narrow ? 5 : 3.5;
                        var block = new BlockTableRecord { Name = name };
                        bt.Add(block); tr.AddNewlyCreatedDBObject(block, true);
                        var labels = title ? new[] { "借通用件登记", "描 图", "旧底图总号", "签 字" } : new[] { "外购", "断料", "委外焊接", "断料" };
                        var translations = title ? new[] { "Common parts register", "Tracing", "Previous drawing No.", "Signature" } : new[] { "Purchased", "Cutting", "Outsourced welding", "Cutting" };
                        for (int i = 0; i < labels.Length; i++)
                        {
                            var y = i * rowHeight;
                            foreach (var line in new[] {
                                new Line(new Point3d(0,y,0), new Point3d(cellWidth,y,0)),
                                new Line(new Point3d(0,y+rowHeight,0), new Point3d(cellWidth,y+rowHeight,0)),
                                new Line(new Point3d(0,y,0), new Point3d(0,y+rowHeight,0)),
                                new Line(new Point3d(cellWidth,y,0), new Point3d(cellWidth,y+rowHeight,0)) })
                            { block.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true); }
                            Entity text = title ? new MText { TextStyleId=style.ObjectId, Contents="\\W1.0732;\\H190;"+labels[i]+"\\H200;", TextHeight=height, Width=0, Attachment=AttachmentPoint.BottomLeft, Location=new Point3d(25,y-9,0), LineSpacingFactor=.903614457831325, LineSpacingStyle=LineSpacingStyle.Exactly } : narrow ? new MText { TextStyleId=style.ObjectId, Contents="{\\W0.9645;"+labels[i]+"}", TextHeight=height, Width=0, Attachment=AttachmentPoint.MiddleLeft, Location=new Point3d(1.7,y+4,0), LineSpacingFactor=.903614457831325, LineSpacingStyle=LineSpacingStyle.Exactly } : i == 2
                                ? new MText { Contents=labels[i], TextHeight=3.5, Width=55, Location=new Point3d(4,y+13,0) }
                                : new DBText { TextString=labels[i], Height=3.5, Position=new Point3d(4,y+8,0) };
                            block.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true);
                            var bounds = text.GeometricExtents;
                            var allowed = AvailableTextSpace.Measure(text,bounds,tr,false,out var length);
                            if (!narrow && !title && i == 0 && length <= bounds.MaxPoint.X-bounds.MinPoint.X+1)
                                throw new InvalidOperationException("Block cell free space was ignored: " + name);
                            // Obstacle snapshots must not cache a half-built fixture.
                            AvailableTextSpace.Refresh(tr);
                            expected[text.Handle.ToString()] = translations[i];
                            items.Add(new TextEntity { Handle=text.Handle.ToString(), PlainText=labels[i], RawText=labels[i],
                                TranslatedText=title ? "\\W1.0732;\\H190;"+translations[i]+"\\H200;" : narrow ? "\\W0.9645;"+translations[i] : translations[i], Height=height, OriginalHeight=height,
                                MTextRectangleWidth=!title && !narrow && i==2?55:0, Status=TranslationStatus.Translated });
                        }
                        var nested = new BlockReference(new Point3d(title?600:narrow?200:name=="*U"?100:0,0,0),block.ObjectId);
                        parent.AppendEntity(nested); tr.AddNewlyCreatedDBObject(nested,true);
                    }
                    foreach (var rotation in new[] {0.0, Math.PI/2})
                    {
                        var insert = new BlockReference(new Point3d(rotation==0?0:400,0,0),parent.ObjectId)
                            { Rotation=rotation, ScaleFactors=new Scale3d(rotation==0?1:2) };
                        model.AppendEntity(insert); tr.AddNewlyCreatedDBObject(insert,true);
                    }
                    tr.Commit();
                }
                db.SaveAs(source,DwgVersion.Current);
            }
            var result = new AcadWriterEngine().WriteTranslations(source,output,items,false);
            report.Add($"WRITEBACK={result.SuccessCount}/{items.Count}");
            report.AddRange(result.Errors);
            if(result.SuccessCount!=items.Count || result.FailCount!=0) throw new InvalidOperationException("Incomplete block writeback");
            using (var db = new Database(false,true))
            {
                db.ReadDwgFile(output,FileOpenMode.OpenForReadAndAllShare,false,null);
                using var tr = db.TransactionManager.StartTransaction();
                foreach(var item in items)
                {
                    var text=(Entity)tr.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(item.Handle,16)),0),OpenMode.ForRead);
                    var actual=text is DBText dt?dt.TextString:((MText)text).Text;
                    if(actual!=expected[item.Handle]) throw new InvalidOperationException("Content mismatch: "+item.Handle+"="+actual);
                    if(AvailableTextSpace.FindIntersections(text,tr).Any()) throw new InvalidOperationException("Block collision: "+item.Handle);
                    if (item.OriginalHeight == 200 && text is MText titleText)
                    {
                        if (titleText.TextHeight < 80) throw new InvalidOperationException("Title text below height floor");
                        if (item.PlainText == "借通用件登记" && titleText.ActualHeight <= titleText.TextHeight*1.8) throw new InvalidOperationException("Long title should wrap, not condense into one line");
                        if (item.PlainText == "描 图" && titleText.TextHeight < 160) throw new InvalidOperationException("Short title was unnecessarily shrunk");
                        report.Add("TITLE_HEIGHT="+titleText.TextHeight+"|LAYOUT_HEIGHT="+titleText.ActualHeight);
                    }
                    report.Add("CONTENT_OK="+item.Handle+"="+actual);
                }
            }
            report.Add("PASS");
        }
        catch(System.Exception ex) { report.Add("FAIL="+ex); }
        File.WriteAllLines(Path.Combine(directory,"block.report"),report);
    }
}
