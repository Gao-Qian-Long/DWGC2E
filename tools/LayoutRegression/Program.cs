using DwgTranslator.Core.Services;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Translation;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length==3 && args[0]=="drawing")
{
    var reader=new DwgReaderService();
    var source=reader.ExtractFromFile(args[1]);
    var output=reader.ExtractFromFile(args[2]).ToDictionary(e=>e.Handle);
    var columns=source.Where(e=>e.EntityType=="MText" && VerticalTextLayout.IsCharacterColumn(
        e.PlainText,e.Rotation,e.MTextRectangleWidth,e.Height)).ToList();
    int rotated=columns.Count(e=>output.TryGetValue(e.Handle,out var value) && Math.Abs(value.Rotation-Math.PI/2)<1e-6);
    Console.WriteLine($"VERTICAL_ROTATED={rotated}/{columns.Count}");
    Console.WriteLine("SHA256="+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))));
    return rotated==columns.Count?0:1;
}
var cachePath=Path.Combine(Path.GetTempPath(),"dwgtranslator-cache-"+Guid.NewGuid().ToString("N")+".json");
var cache=new TranslationConsistencyService(cachePath);
cache.AddToCache("轴承","Bearing","ZH>EN");
var enHit=cache.TryGetMatch("轴承","ZH>EN",out var enValue);
var deHit=cache.TryGetMatch("轴承","ZH>DE",out _);

var cases=new Dictionary<string,bool>
{
    ["narrow CJK column"]=VerticalTextLayout.IsCharacterColumn("报警灯",0,10.46,9.24),
    ["identifier column"]=VerticalTextLayout.IsCharacterColumn("35A共挤启动",0,7.63,9.24),
    ["hard breaks"]=VerticalTextLayout.IsCharacterColumn("报\r\n警\r\n灯",0,50,9.24),
    ["ordinary paragraph"]=!VerticalTextLayout.IsCharacterColumn("报警状态\n真空泵运行",0,50,9.24),
    ["English narrow label"]=!VerticalTextLayout.IsCharacterColumn("Alarm light",0,7,9),
    ["preserve existing rotation"]=!VerticalTextLayout.IsCharacterColumn("报警灯",Math.PI/2,10,9),
    ["invalid height"]=!VerticalTextLayout.IsCharacterColumn("报警灯",0,10,0),
    ["vertical source applies to Latin target"]=VerticalTextLayout.IsCharacterColumn("共挤启动",0,7.63,9.24)
        && VerticalTextLayout.TargetRotation(0,false)==Math.PI/2
        && VerticalTextLayout.FormatTranslatedColumn("Co-extrusion Startup",false)=="Co-extrusion Startup",
    ["CJK target remains upright and stacked"]=VerticalTextLayout.TargetRotation(0,true)==0
        && VerticalTextLayout.FormatTranslatedColumn("35A共挤启动",true)==@"35A\P共\P挤\P启\P动",
    ["CJK to Latin inline font mapping"]=FontMapper.MapInlineFonts(@"{\fSimSun|b1|i0|c134|p54;Alarm}",false)==@"{\fArial|b1|i0;Alarm}",
    ["Latin to CJK inline font mapping"]=FontMapper.MapInlineFonts(@"{\fArial|b1|i0;报警}",true)==@"{\fSimHei|b1|i0;报警}",
    ["unknown font preserved"]=FontMapper.MapInlineFonts(@"\fCustomFont|b0;ABC",false)==@"\fCustomFont|b0;ABC",
    ["page number slot"]=CadLabelCompactor.Compact("共      页","Total Sheets")=="Sheets",
    ["equipment ID retained"]=CadLabelCompactor.Compact("35B共挤转速测量","long label")=="35B coextr. speed meas.",
    ["ordinary translation unchanged"]=CadLabelCompactor.Compact("报警灯","Alarm light")=="Alarm light"
    ,["cache direction isolation"]=enHit && enValue=="Bearing" && !deHit
    ,["punctuated echo rejected"]=!TranslationQualityValidator.IsAcceptable("Motor stop","Motor stop!","EN","DE")
    ,["pair-specific glossary name"]=TranslationLanguages.GlossaryFileName("ZH","DE")=="mechanical_zh_de.json"
    ,["config writes camelCase"]=JsonSerializer.Serialize(new AppConfig(),AppConfigJson.WriteOptions).Contains("\"deepSeekApiKey\"")
    ,["cell border clearance scales with text height"]=Math.Abs(WritebackConstants.GeometryClearance(10)-.4)<1e-9
    ,["cell border clearance has absolute floor"]=Math.Abs(WritebackConstants.GeometryClearance(.1)-.02)<1e-9
};
foreach(var pair in cases.Where(p=>!p.Value))Console.WriteLine("FAIL="+pair.Key);
Console.WriteLine($"LAYOUT_CASES={cases.Count(p=>p.Value)}/{cases.Count}");
try { File.Delete(cachePath); } catch { }
return cases.All(p=>p.Value)?0:1;
