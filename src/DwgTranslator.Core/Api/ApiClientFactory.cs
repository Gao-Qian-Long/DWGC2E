using System.Net.Http;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Api;

/// <summary>Creates the only supported production API client: the Cloudflare Worker.</summary>
public static class ApiClientFactory
{
    public static IApiClient Create(AppConfig config, HttpClient httpClient,
        Func<string?> tokenProvider, string deviceId, string deviceName,
        Func<IApiClient>? legacyDirectFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);
        config.ApiMode = "worker";
        return new WorkerApiClient(httpClient, config.ApiBaseUrl, config.UpdateManifestUrl,
            tokenProvider, deviceId, deviceName);
    }
}
