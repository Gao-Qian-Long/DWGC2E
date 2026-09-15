using System.Threading;
using System.Threading.Tasks;
namespace DwgTranslator.Core.Api;
/// <summary>Optional capability; existing IApiClient implementations remain compatible.</summary>
public interface IAccountSessionClient
{
    Task<bool> LogoutAsync(CancellationToken cancellationToken = default);
}
