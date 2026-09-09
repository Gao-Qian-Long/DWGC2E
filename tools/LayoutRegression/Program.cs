using DwgTranslator.Core.Services;
using System.Security.Cryptography;

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
var cases=new Dictionary<string,bool>
{
    ["narrow CJK column"]=VerticalTextLayout.IsCharacterColumn("报警灯",0,10.46,9.24),
    ["identifier column"]=VerticalTextLayout.IsCharacterColumn("35A共挤启动",0,7.63,9.24),
    ["hard breaks"]=VerticalTextLayout.IsCharacterColumn("报\r\n警\r\n灯",0,50,9.24),
    ["ordinary paragraph"]=!VerticalTextLayout.IsCharacterColumn("报警状态\n真空泵运行",0,50,9.24),
    ["English narrow label"]=!VerticalTextLayout.IsCharacterColumn("Alarm light",0,7,9),
    ["preserve existing rotation"]=!VerticalTextLayout.IsCharacterColumn("报警灯",Math.PI/2,10,9),
    ["invalid height"]=!VerticalTextLayout.IsCharacterColumn("报警灯",0,10,0),
    ["inline font mapping"]=FontMapper.MapInlineFonts(@"{\fSimSun|b1|i0|c134|p54;Alarm}",true)==@"{\fArial|b1|i0;Alarm}",
    ["unknown font preserved"]=FontMapper.MapInlineFonts(@"\fCustomFont|b0;ABC",true)==@"\fCustomFont|b0;ABC",
    ["page number slot"]=CadLabelCompactor.Compact("共      页","Total Sheets")=="Sheets",
    ["equipment ID retained"]=CadLabelCompactor.Compact("35B共挤转速测量","long label")=="35B coextr. speed meas.",
    ["ordinary translation unchanged"]=CadLabelCompactor.Compact("报警灯","Alarm light")=="Alarm light"
};
foreach(var pair in cases.Where(p=>!p.Value))Console.WriteLine("FAIL="+pair.Key);
Console.WriteLine($"LAYOUT_CASES={cases.Count(p=>p.Value)}/{cases.Count}");
return cases.All(p=>p.Value)?0:1;
