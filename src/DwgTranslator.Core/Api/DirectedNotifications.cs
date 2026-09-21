using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace DwgTranslator.Core.Api;

/// <summary>
/// One directed (per-user) notification, as returned by <c>GET /v1/notifications</c>
/// (CONTRACT-notifications.md §3.2). <see cref="ReadAt"/> is the only read indicator:
/// <c>null</c> means unread, a value means the first read time was already recorded server-side.
/// </summary>
/// <remarks>
/// A record rather than a class on purpose: the ViewModel needs <c>with</c> to flip
/// <see cref="ReadAt"/> optimistically when the user opens a notification without mutating the
/// snapshot instance that other callers still hold.
/// </remarks>
public sealed record DirectedNotification
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime? CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? ReadAt { get; init; }

    public bool IsUnread => ReadAt == null;

    /// <summary>Title and body joined for display; a notification always has a non-empty body.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Title) ? Body : Title + "\n" + Body;
}

/// <summary>One page of directed notifications. The server caps the page at 25 items.</summary>
public sealed class DirectedNotificationFeed
{
    public static readonly DirectedNotificationFeed Empty = new();

    public IReadOnlyList<DirectedNotification> Items { get; init; } = Array.Empty<DirectedNotification>();
    public int UnreadCount { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>
/// Why a directed-notification read ended the way. The distinct values exist so the caller can
/// tell "we never asked" from "the server said no" without inspecting exception types — the bell keeps
/// showing the site-wide announcement in every non-<see cref="Loaded"/> case.
/// </summary>
public enum DirectedNotificationStatus
{
    /// <summary>No verified session (or the caller cancelled): no request was sent.</summary>
    NotRequested,

    /// <summary>The configured API client has no directed-notification capability (e.g. a test double).</summary>
    Unsupported,

    /// <summary>The feed was read successfully.</summary>
    Loaded,

    /// <summary>The server rejected the session (HTTP 401).</summary>
    Unauthenticated,

    /// <summary>The transport failed (DNS, TCP, TLS, timeout): the server was never reached.</summary>
    Unavailable,

    /// <summary>The server answered with a non-success status or an unparseable body.</summary>
    Failed
}

public sealed class DirectedNotificationOutcome
{
    public static DirectedNotificationOutcome NotRequested() => new() { Status = DirectedNotificationStatus.NotRequested };

    public DirectedNotificationStatus Status { get; init; }
    public DirectedNotificationFeed Feed { get; init; } = DirectedNotificationFeed.Empty;
    public string? ErrorCode { get; init; }

    public bool Succeeded => Status == DirectedNotificationStatus.Loaded;
}

/// <summary>
/// Optional capability, mirroring <see cref="IAccountSessionClient"/> and
/// <see cref="IIncrementalCloudGlossaryClient"/>: existing <see cref="IApiClient"/> implementations
/// stay compatible because the ViewModel only talks to this interface when a client happens to
/// implement it. Implementations MUST reuse the client's shared <c>HttpClient</c>.
/// </summary>
public interface IDirectedNotificationClient
{
    /// <summary>Reads the signed-in user's own notifications. Never returns another user's rows.</summary>
    Task<DirectedNotificationOutcome> ReadDirectedNotificationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports read receipts for <paramref name="ids"/>; the call is idempotent server-side.</summary>
    Task<bool> ReportDirectedNotificationsReadAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
}

/// <summary>
/// Degradation policy for directed notifications, kept in Core so it is testable without WPF.
///
/// Design rule: this class is the single place where the three degradation paths are decided, and it
/// never throws and never touches the UI. <see cref="ReadAsync"/> returns
/// <see cref="DirectedNotificationStatus.NotRequested"/> without any network call when there is no
/// verified session, and folds a transport failure, a 401 and a malformed body into
/// <see cref="DirectedNotificationStatus.Unavailable"/> /
/// <see cref="DirectedNotificationStatus.Unauthenticated"/> /
/// <see cref="DirectedNotificationStatus.Failed"/> respectively. The caller therefore has no failure
/// branch that could surface an error toast or interrupt the main workflow.
/// </summary>
public sealed class DirectedNotificationService
{
    private readonly IDirectedNotificationClient? _client;
    private readonly Func<bool> _hasSession;

    public DirectedNotificationService(IApiClient? client, Func<bool> hasSession)
    {
        _client = client as IDirectedNotificationClient;
        _hasSession = hasSession ?? throw new ArgumentNullException(nameof(hasSession));
    }

    /// <summary>False when the configured client cannot serve directed notifications at all.</summary>
    public bool IsSupported => _client != null;

    public async Task<DirectedNotificationOutcome> ReadAsync(CancellationToken cancellationToken = default)
    {
        // Degradation path 1 (未登录 / no token): the guard runs before any HTTP work, so a signed-out
        // client never even builds a request — and therefore can never send a stale credential.
        if (!_hasSession())
            return DirectedNotificationOutcome.NotRequested();

        if (_client == null)
            return new DirectedNotificationOutcome { Status = DirectedNotificationStatus.Unsupported };

        try
        {
            return await _client.ReadDirectedNotificationsAsync(cancellationToken).ConfigureAwait(false)
                ?? new DirectedNotificationOutcome { Status = DirectedNotificationStatus.Unavailable };
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal shutdown/refresh race, not a failure: the bell keeps its snapshot.
            return DirectedNotificationOutcome.NotRequested();
        }
        catch (Exception ex)
        {
            // Degradation paths 2 and 3 (断网 / 401) normally arrive as statuses from the client; this is
            // the last-resort net so a surprise exception can never reach the UI thread.
            Log.Warning("定向通知读取失败：{ErrorType}", ex.GetType().Name);
            return new DirectedNotificationOutcome { Status = DirectedNotificationStatus.Unavailable };
        }
    }

    /// <summary>
    /// Reports read receipts. Returns false on any failure instead of throwing, because the caller
    /// clears the unread dot optimistically first (CONTRACT-notifications.md §8: "本地先展示，
    /// 服务端失败下次重试").
    /// </summary>
    public async Task<bool> ReportReadAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Count == 0) return false;
        if (!_hasSession() || _client == null) return false;

        try
        {
            return await _client.ReportDirectedNotificationsReadAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning("定向通知已读回传失败：{ErrorType}", ex.GetType().Name);
            return false;
        }
    }
}
