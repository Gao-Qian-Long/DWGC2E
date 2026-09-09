using System.Text.RegularExpressions;
namespace DwgTranslator.Core.Services;

/// <summary>Conventional compact drawing labels; never truncates arbitrary text.</summary>
public static class CadLabelCompactor
{
    public static string Compact(string source, string translated)
    {
        var key=source.Trim();
        // The number is a separate CAD entity occupying the blank in this label.
        // The layout solver places "Sheets" after it, e.g. "19 Sheets".
        if(Regex.IsMatch(key,@"^共\s+页$"))return "Sheets";
        if(key.EndsWith("电路图",StringComparison.Ordinal))
            return translated.Replace("Circuit Diagram","Schematic",StringComparison.OrdinalIgnoreCase);
        switch(key)
        {
            case "处数": return "Qty.";
            case "制图": return "Drawn";
            case "旧底图总号": return "Prev. Base No.";
        }
        if(Regex.IsMatch(key,@"^日光灯\*\d+$"))
            return "Fluor. lamp" + key.Substring(3);
        var pump=Regex.Match(key,@"^(\d+)机真空泵$");
        if(pump.Success) return pump.Groups[1].Value + " Vac. pump";
        var barrel=Regex.Match(key,@"^主机料筒([一二三四])加热$");
        if(barrel.Success)
            return "Main barrel " + ("一二三四".IndexOf(barrel.Groups[1].Value,StringComparison.Ordinal)+1) + " heating";
        var current=Regex.Match(key,@"^(\d+[A-Z])共挤电流测量$");
        if(current.Success) return current.Groups[1].Value + " coextr. current meas.";
        var speed=Regex.Match(key,@"^(\d+[A-Z])共挤转速测量$");
        if(speed.Success) return speed.Groups[1].Value + " coextr. speed meas.";
        return translated;
    }
}
