#if GSTARCAD
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.Geometry;
using Gssoft.Gscad.PlottingServices;
using Gssoft.Gscad.Runtime;
using GsPlotType=Gssoft.Gscad.DatabaseServices.PlotType;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using GsPlotType=Autodesk.AutoCAD.DatabaseServices.PlotType;
#endif
[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.LayoutPlotCommand))]
namespace DwgTranslator.Cad.Commands;
public class LayoutPlotCommand
{
    [CommandMethod("DWGPLOTREGIONS",CommandFlags.Session)]
    public void Execute()
    {
        var input=Application.DocumentManager.MdiActiveDocument.Editor.GetString("\nRequest: ").StringResult.Trim('"');
        var report=new List<string>();
        try
        {
            var lines=File.ReadAllLines(input);
            var doc=Application.DocumentManager.Open(lines[0],true);
            Application.DocumentManager.MdiActiveDocument=doc;
            using(var docLock=doc.LockDocument())
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var model=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),OpenMode.ForRead);
                var layout=(Layout)tr.GetObject(model.LayoutId,OpenMode.ForRead);
                var validator=PlotSettingsValidator.Current;
                foreach(var region in lines.Skip(2))
                {
                    var fields=region.Split('|');
                    double N(int i)=>double.Parse(fields[i],System.Globalization.CultureInfo.InvariantCulture);
                    using var settings=new PlotSettings(layout.ModelType);
                    settings.CopyFrom(layout);
                    validator.SetPlotConfigurationName(settings,"DWG To PDF.pc3",null);
                    validator.RefreshLists(settings);
                    var papers=validator.GetCanonicalMediaNameList(settings).Cast<string>().ToList();
                    report.Add("PAPERS="+string.Join(",",papers));
                    var media=papers.FirstOrDefault(n=>n.Contains("A3")) ?? papers.First();
                    validator.SetCanonicalMediaName(settings,media);
                    validator.SetPlotPaperUnits(settings,PlotPaperUnit.Millimeters);
                    validator.SetPlotRotation(settings,PlotRotation.Degrees000);
                    validator.SetPlotWindowArea(settings,new Extents2d(N(1),N(2),N(3),N(4)));
                    validator.SetPlotType(settings,GsPlotType.Window);
                    validator.SetUseStandardScale(settings,true);
                    validator.SetStdScaleType(settings,StdScaleType.ScaleToFit);
                    validator.SetPlotCentered(settings,true);
                    using var info=new PlotInfo{Layout=model.LayoutId,OverrideSettings=settings};
                    using var infoValidator=new PlotInfoValidator{MediaMatchingPolicy=MatchingPolicy.MatchEnabled};
                    infoValidator.Validate(info);
                    using var engine=PlotFactory.CreatePublishEngine();
                    engine.BeginPlot(null,null);
                    var path=Path.Combine(lines[1],fields[0]+".pdf");
                    engine.BeginDocument(info,doc.Name,null,1,true,path);
                    using var page=new PlotPageInfo();
                    engine.BeginPage(page,info,true,null);
                    engine.BeginGenerateGraphics(null);engine.EndGenerateGraphics(null);
                    engine.EndPage(null);engine.EndDocument(null);engine.EndPlot(null);
                    report.Add("PLOT="+path+"|BYTES="+new FileInfo(path).Length);
                }
            }
        }catch(System.Exception ex){report.Add("ERROR="+ex);}
        File.WriteAllLines(input+".report",report);
    }
}
