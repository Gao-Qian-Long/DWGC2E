namespace DwgTranslator.Core.Api;

/// <summary>Only the former product default is migrated; arbitrary user endpoints are untouched.</summary>
public static class ProductApiEndpoint
{
    public const string Default = "https://api.cad.pocketter.dpdns.org";
    private const string FormerDefault = "https://dwgc2e-api.maplehousezz.workers.dev";

    public static string Migrate(string address) => string.Equals(address?.Trim().TrimEnd('/'), FormerDefault, StringComparison.OrdinalIgnoreCase)
        ? Default : address!;

    /// <summary>
    /// 只接受 HTTPS 后端地址（本地回环调试除外）。令牌与全部译文都发往该地址，
    /// 一旦配置被改写而未校验，流量（含 Bearer 令牌）就会被导向任意服务器。
    /// </summary>
    public static bool IsAllowed(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
            return !string.IsNullOrEmpty(uri.Host);
        // 明文 HTTP 仅允许本机调试。
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}
