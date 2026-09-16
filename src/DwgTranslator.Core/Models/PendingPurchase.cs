namespace DwgTranslator.Core.Models;

/// <summary>Durable checkout intent. Property names retain the existing purchase recovery JSON contract.</summary>
public sealed class PendingPurchase
{
    public string PlanId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Key { get; set; } = "";
    public string? OrderNo { get; set; }
}
