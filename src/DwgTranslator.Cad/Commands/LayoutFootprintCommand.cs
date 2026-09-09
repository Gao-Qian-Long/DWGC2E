#if GSTARCAD
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.Runtime;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
#endif
[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.LayoutFootprintCommand))]
namespace DwgTranslator.Cad.Commands;
public class LayoutFootprintCommand
{
    [CommandMethod("DWGFOOTPRINT")]
    public void Execute()
    {
        var input=Application.DocumentManager.MdiActiveDocument.Editor.GetString("\nRequest: ").StringResult.Trim('"');
        var report=new List<string>();
        try {
            var lines=File.ReadAllLines(input);
            foreach(var path in lines.Take(3)) {
                using var db=new Database(false,true);
                db.ReadDwgFile(path,FileOpenMode.OpenForReadAndAllShare,false,null);
                using var tr=db.TransactionManager.StartTransaction();
                report.Add("FILE="+path);
                foreach(var handle in lines[3].Split(',')) {
                    var e=(Entity)tr.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0),OpenMode.ForRead);
                    report.Add("HANDLE="+handle+"|BOX="+e.GeometricExtents);
                    var actual=Replacement.CollisionDetector.GetCorrectedBounds(e);
                    bool column=e is MText mm && DwgTranslator.Core.Services.VerticalTextLayout.IsCharacterColumn(mm.Text,mm.Rotation,mm.Width,mm.TextHeight);
                    report.Add("INK="+actual+"|ALLOWED="+Replacement.AvailableTextSpace.Measure(e,actual,tr,column));
                    if(e is MText m) {
                        report.Add($"MTEXT={m.Contents}|ROT={m.Rotation}|WIDTH={m.Width}|ACTUAL={m.ActualWidth},{m.ActualHeight}|LOCATION={m.Location}|ATTACH={m.Attachment}");
                        var pieces=new DBObjectCollection();m.Explode(pieces);
                        foreach(DBObject p in pieces) {using(p) if(p is Entity part)report.Add("PIECE="+part.GetType().Name+"|BOX="+part.GeometricExtents+(part is DBText t ? "|TEXT="+t.TextString : ""));}
                    }
                }
            }
        }catch(System.Exception ex){report.Add("ERROR="+ex);}
        File.WriteAllLines(input+".report",report);
    }
}
