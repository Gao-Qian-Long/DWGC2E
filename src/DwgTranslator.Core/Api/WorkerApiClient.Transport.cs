using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
namespace DwgTranslator.Core.Api;
public sealed partial class WorkerApiClient
{
    private async Task<HttpOutcome> SendAsync(
        HttpMethod method,
        string requestUri,
        HttpContent? content,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool includeAuth = true,
        string? idempotencyKey = null)
    {
        var outcome = new HttpOutcome();
        var sessionAtStart = includeAuth ? GetToken() : null;

        // 组合调用方令牌与本地超时：调用方取消要能中断，单次请求也不会无限挂着。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(method, requestUri);
            if (content != null) request.Content = content;
            if (idempotencyKey != null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

            if (includeAuth)
            {
                var token = sessionAtStart;
                if (!string.IsNullOrEmpty(token))
                {
                    // 逐请求挂 Authorization，不改 HttpClient.DefaultRequestHeaders：
                    // 共享 HttpClient 时改默认头会与并发请求互相踩，而且令牌一旦进默认头就很难保证不被日志带出去。
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                }
            }

            // 用 SendAsync 而不是 GetFromJsonAsync/PostAsJsonAsync：后两个在 net48 上不可用。
            using var response = await _httpClient.SendAsync(request, linked.Token).ConfigureAwait(false);
            outcome.StatusCode = (int)response.StatusCode;
            outcome.Body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (includeAuth && !string.Equals(sessionAtStart, GetToken(), StringComparison.Ordinal))
                throw new OperationCanceledException("账号会话已变更，已丢弃旧响应。");

            if (response.Headers.TryGetValues("X-Chars-Used", out var values))
                outcome.CharactersUsedHeader = values.FirstOrDefault();
        }
        catch (OperationCanceledException) when (includeAuth && !string.Equals(sessionAtStart, GetToken(), StringComparison.Ordinal))
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消：原样上抛，保持取消语义（上层靠它中止整批任务）。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 本地超时（或 HttpClient.Timeout 触发）。契约角度这是"上游没能及时响应"。
            outcome.TransportFailed = true;
            outcome.TransportErrorCode = "upstream_unavailable";
            outcome.TransportMessage = "请求超时";
            Log.Warning("{Method} {Uri} 超时（{Seconds}s）", method.Method, requestUri, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            // HttpRequestException / SocketException / IOException / UriFormatException 等
            // 都归到 network_error；只记异常类型与消息，响应体和令牌不入日志。
            outcome.TransportFailed = true;
            outcome.TransportErrorCode = "network_error";
            outcome.TransportMessage = ex.Message;
            Log.Warning(ex, "{Method} {Uri} 网络异常", method.Method, requestUri);
        }

        return outcome;
    }

}
