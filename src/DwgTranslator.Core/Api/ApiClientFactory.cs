using System.Net.Http;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Api;

/// <summary>Only an explicit direct setting may activate a client-held model key.</summary>
public static class ApiClientFactory
{
    public static IApiClient Create(AppConfig config, HttpClient httpClient,
        Func<string?> tokenProvider, string deviceId, string deviceName,
        Func<IApiClient> createDirect)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.Equals(config.ApiMode?.Trim(), "direct", StringComparison.OrdinalIgnoreCase))
            return createDirect();

        // Missing, misspelled or invalid Worker configuration must never silently use a local key.
        return new WorkerApiClient(httpClient, config.ApiBaseUrl, config.UpdateManifestUrl,
            tokenProvider, deviceId, deviceName);
    }
}
