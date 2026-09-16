namespace DwgTranslator.Core.Api;

/// <summary>Only the former product default is migrated; arbitrary user endpoints are untouched.</summary>
public static class ProductApiEndpoint
{
    public const string Default = "https://api.cad.pocketter.dpdns.org";
    private const string FormerDefault = "https://dwgc2e-api.maplehousezz.workers.dev";

    public static string Migrate(string address) => string.Equals(address?.Trim().TrimEnd('/'), FormerDefault, StringComparison.OrdinalIgnoreCase)
        ? Default : address!;
}
