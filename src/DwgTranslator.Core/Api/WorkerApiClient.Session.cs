using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace DwgTranslator.Core.Api;
public sealed partial class WorkerApiClient
{
    public async Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return false;
        var session = GetToken();
        var result = await SendAsync(HttpMethod.Post, Url("/v1/auth/logout"), null, DefaultTimeout, cancellationToken, expectedSession: session).ConfigureAwait(false);
        var revoked = !result.TransportFailed && (result.IsSuccess || result.StatusCode == 401);
        if (revoked) _ = RejectSession(session);
        return revoked;
    }
}
