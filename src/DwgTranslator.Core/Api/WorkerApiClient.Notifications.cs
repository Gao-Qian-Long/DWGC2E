using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace DwgTranslator.Core.Api;

/// <summary>
/// User-side directed notifications (CONTRACT-notifications.md §1.1). Both endpoints ride the shared
/// <see cref="HttpClient"/> and the same <c>SendAsync</c> transport as every other call, so the bearer
/// is attached per request and never placed in <c>DefaultRequestHeaders</c>.
/// </summary>
public sealed partial class WorkerApiClient : IDirectedNotificationClient
{
    private const int DirectedNotificationPageSize = 25;

    /// <inheritdoc />
    public async Task<DirectedNotificationOutcome> ReadDirectedNotificationsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new DirectedNotificationOutcome { Status = DirectedNotificationStatus.Unavailable, ErrorCode = UnconfiguredErrorCode };

        // No session → no request. This is the "未登录" degradation path: the caller passes a token
        // provider that returns null/empty, so the endpoint is never hit with an anonymous request
        // (which the gateway would answer with a pointless 401 and an error log line every poll).
        var session = GetToken();
        if (string.IsNullOrWhiteSpace(session))
            return DirectedNotificationOutcome.NotRequested();

        var outcome = await SendAsync(HttpMethod.Get, Url("/v1/notifications"), null, DefaultTimeout, cancellationToken,
            expectedSession: session).ConfigureAwait(false);

        if (outcome.TransportFailed)
        {
            // "断网" path: unreachable base URL / DNS / TCP / TLS / timeout. The server was never reached.
            Log.Debug("定向通知拉取失败：{ErrorCode}", outcome.TransportErrorCode);
            return new DirectedNotificationOutcome { Status = DirectedNotificationStatus.Unavailable, ErrorCode = outcome.TransportErrorCode };
        }

        if (outcome.StatusCode == 401)
        {
            // 401 is returned as a status, deliberately NOT thrown as ApiAuthenticationException.
            // Rationale: this read runs from a one-minute background timer, and the caller's job is to
            // degrade silently. Throwing would force the banner to catch an exception on every poll and
            // would make a transient 401 indistinguishable from a real read failure in the UI.
            // Note the shared transport still fires AuthenticationRejected once per rejected token
            // (WorkerApiClient.Transport.cs:89), which is what lets the account area notice expiry; the
            // rejected-token cache means the timer does not re-notify on every subsequent poll.
            Log.Debug("定向通知拉取被拒绝：HTTP 401");
            return new DirectedNotificationOutcome { Status = DirectedNotificationStatus.Unauthenticated, ErrorCode = "unauthenticated" };
        }

        if (!outcome.IsSuccess)
        {
            var (code, _) = ReadError(outcome);
            // 500 internal_error is what the user-side endpoint returns when migration 0023 is missing
            // (CONTRACT-notifications.md §4: the user side never answers 503). Treated as a plain failure.
            Log.Debug("定向通知拉取返回 HTTP {Status} {ErrorCode}", outcome.StatusCode, code);
            return new DirectedNotificationOutcome { Status = DirectedNotificationStatus.Failed, ErrorCode = code };
        }

        var wire = Deserialize<WireDirectedNotificationFeed>(outcome.Body);
        if (wire == null)
            return new DirectedNotificationOutcome { Status = DirectedNotificationStatus.Failed, ErrorCode = "invalid_response" };

        var items = (wire.Items ?? new List<WireDirectedNotification>())
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
            .Take(DirectedNotificationPageSize)
            .Select(x => new DirectedNotification
            {
                Id = x!.Id!,
                Title = (x.Title ?? "").Trim(),
                Body = (x.Body ?? "").Trim(),
                CreatedAt = ParseTimestamp(x.CreatedAt),
                ExpiresAt = ParseTimestamp(x.ExpiresAt),
                ReadAt = ParseTimestamp(x.ReadAt)
            })
            .Where(x => x.Body.Length > 0 || x.Title.Length > 0)
            .ToList();

        // unread_count comes from the server and counts rows the client did not receive (the feed is
        // paged), so never replace it with a local count over `items`.
        return new DirectedNotificationOutcome
        {
            Status = DirectedNotificationStatus.Loaded,
            Feed = new DirectedNotificationFeed
            {
                Items = items,
                UnreadCount = Math.Max(0, wire.UnreadCount ?? items.Count(x => x.IsUnread)),
                HasMore = wire.HasMore ?? false
            }
        };
    }

    /// <inheritdoc />
    public async Task<bool> ReportDirectedNotificationsReadAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || ids == null || ids.Count == 0) return false;

        var session = GetToken();
        if (string.IsNullOrWhiteSpace(session)) return false;

        // The endpoint accepts 1..100 ids and rejects the whole batch if any entry is not a uuid, so a
        // malformed id must be filtered here rather than sent (it would fail the valid ones with it).
        var valid = ids.Where(x => !string.IsNullOrWhiteSpace(x) && Guid.TryParseExact(x, "D", out _))
            .Distinct(StringComparer.Ordinal).Take(100).ToList();
        if (valid.Count == 0) return false;

        var outcome = await SendAsync(HttpMethod.Post, Url("/v1/notifications/read"), JsonContent(new WireReadRequest { Ids = valid }),
            DefaultTimeout, cancellationToken, expectedSession: session).ConfigureAwait(false);

        if (outcome.TransportFailed || outcome.StatusCode != 200)
        {
            Log.Debug("定向通知已读回传未生效：HTTP {Status} {ErrorCode}", outcome.StatusCode,
                outcome.TransportFailed ? outcome.TransportErrorCode : ReadError(outcome).Code);
            return false;
        }

        var wire = Deserialize<WireReadResponse>(outcome.Body);
        return wire?.Success == true;
    }

    private sealed class WireDirectedNotification
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
        [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
        [JsonPropertyName("read_at")] public string? ReadAt { get; set; }
    }

    private sealed class WireDirectedNotificationFeed
    {
        [JsonPropertyName("items")] public List<WireDirectedNotification>? Items { get; set; }
        [JsonPropertyName("unread_count")] public int? UnreadCount { get; set; }
        [JsonPropertyName("hasMore")] public bool? HasMore { get; set; }
    }

    private sealed class WireReadRequest
    {
        [JsonPropertyName("ids")] public List<string> Ids { get; set; } = new();
    }

    private sealed class WireReadResponse
    {
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("marked")] public int? Marked { get; set; }
    }
}
