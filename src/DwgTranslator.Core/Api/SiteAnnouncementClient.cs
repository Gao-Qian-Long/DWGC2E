using System.Net.Http;
using System.Text.Json;

namespace DwgTranslator.Core.Api;

/// <summary>Reads public site content without an account credential.</summary>
public static class SiteAnnouncementClient
{
    public static Uri GetEndpoint(string baseUrl)
    {
        var address = ProductApiEndpoint.Migrate(baseUrl).Trim().TrimEnd('/');
        if (address.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) address = address[..^3];
        if (!ProductApiEndpoint.IsAllowed(address)
            || !Uri.TryCreate(address + "/v1/site", UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("公告服务地址无效。");
        return endpoint;
    }

    public static async Task<string> ReadAsync(HttpClient http, string baseUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GetEndpoint(baseUrl));
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("announcement", out var announcement)
            || announcement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("公告响应格式不正确。");
        var text = (announcement.GetString() ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return text[..Math.Min(text.Length, 2000)];
    }
}
