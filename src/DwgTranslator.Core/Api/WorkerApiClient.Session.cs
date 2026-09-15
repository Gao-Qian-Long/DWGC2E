using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace DwgTranslator.Core.Api;
public sealed partial class WorkerApiClient
{
    public async Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return false;
        var result = await SendAsync(HttpMethod.Post, Url("/v1/auth/logout"), null, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return !result.TransportFailed && (result.IsSuccess || result.StatusCode == 401);
    }
}
