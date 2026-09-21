using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DwgTranslator.Core.Api;
using Serilog;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// Two independent announcement channels share one bell:
///
/// 1. The site-wide announcement (<c>GET /v1/site</c>) — public, no credential, single
///    <c>announcement-read.txt</c> fingerprint. Unchanged by directed notifications.
/// 2. Directed per-user notifications (<c>GET /v1/notifications</c>) — requires a verified session.
///
/// Compatibility rule (CONTRACT-notifications.md §8): the site-wide fingerprint file keeps its exact
/// old meaning and format, and directed notifications never read or write it. That is what makes the
/// old single-fingerprint state harmless: the file can only ever mark the site-wide notice as read.
/// Directed read state is multi-row and server-authoritative (<c>read_at</c> per row), so an existing
/// <c>announcement-read.txt</c> cannot suppress a directed notification, and opening a directed
/// notification cannot mark the site-wide notice read.
/// </summary>
public partial class MainViewModel
{
    private string? _readAnnouncement;

    // Directed-notification snapshot, replaced as a whole so readers always see a consistent feed.
    private DirectedNotificationFeed _directedNotifications = DirectedNotificationFeed.Empty;

    // Read receipts the server has not confirmed yet; replayed by the next successful refresh.
    private readonly HashSet<string> _pendingReadReceipts = new(StringComparer.Ordinal);

    // Consecutive failures, used only to back off polling while the backend is missing or unreachable.
    private int _directedNotificationFailures;

    private string AnnouncementReadPath => Path.Combine(App.AppDataDir, "announcement-read.txt");

    // The banner polls this every minute; a client per tick leaves a socket in TIME_WAIT each time.
    // ReadAsync only builds per-request messages, so one shared client is safe across calls.
    // This client is anonymous by construction and MUST stay that way: /v1/site is a public endpoint,
    // and sending an account bearer to it would leak the credential. Directed notifications therefore
    // do not use this client — they go through _apiClient, which owns the session.
    private static readonly HttpClient AnnouncementHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<string> ReadSiteAnnouncementAsync(CancellationToken cancellationToken)
    {
        return await SiteAnnouncementClient.ReadAsync(AnnouncementHttp, _config.ApiBaseUrl, cancellationToken);
    }

    private string AnnouncementFingerprint(string content) => DesktopNotificationPolicy.AnnouncementFingerprint(
        SiteAnnouncementClient.GetEndpoint(_config.ApiBaseUrl).AbsoluteUri, content);

    /// <summary>Unread state of the site-wide announcement. Unrelated to directed notifications.</summary>
    public bool IsAnnouncementUnread(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        try { _readAnnouncement ??= File.Exists(AnnouncementReadPath) ? File.ReadAllText(AnnouncementReadPath).Trim() : ""; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        return _readAnnouncement != AnnouncementFingerprint(content);
    }

    public void MarkAnnouncementRead(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        _readAnnouncement = AnnouncementFingerprint(content);
        try
        {
            Directory.CreateDirectory(App.AppDataDir);
            var temporary = AnnouncementReadPath + ".tmp";
            File.WriteAllText(temporary, _readAnnouncement);
            File.Move(temporary, AnnouncementReadPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Warning("公告已读状态未能保存：{ErrorType}", ex.GetType().Name); }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 定向通知（S10）
    // ───────────────────────────────────────────────────────────────────────

    // Built from the existing client and guard; no new service layer, no new HttpClient.
    // The guard is the same HasSavedAccountSession predicate the account area uses
    // (_config.AuthTokenEncrypted + AppConfig.DecryptApiKey), so "登录过" and "能读自己的通知" cannot
    // drift apart into two different notions of a session.
    // Deliberately NOT cached: _apiClient is a swappable field (tests replace it via reflection), so a
    // captured client reference would keep serving a stale capability/identity after a swap.
    private DirectedNotificationService CreateDirectedNotificationService() =>
        new(_apiClient, () => HasSavedAccountSession);

    /// <summary>The signed-in user's own notifications; empty when signed out or unavailable.</summary>
    public IReadOnlyList<DirectedNotification> DirectedNotifications => _directedNotifications.Items;

    /// <summary>
    /// True when at least one directed notification is unread. <c>unread_count</c> is authoritative
    /// because the feed is paged; the local count is a fallback for a response that omitted it.
    /// </summary>
    public bool HasUnreadDirectedNotifications =>
        _directedNotifications.UnreadCount > 0 || _directedNotifications.Items.Any(x => x.IsUnread);

    /// <summary>Client-supplied copy of the three degradation outcomes, for diagnostics only.</summary>
    public DirectedNotificationStatus LastDirectedNotificationStatus { get; private set; } = DirectedNotificationStatus.NotRequested;

    /// <summary>
    /// Reads directed notifications. Never throws: an absent session, an unreachable backend and a
    /// rejected token all come back as a non-<c>Loaded</c> status and leave the banner's site-wide
    /// snapshot untouched. Runs on a one-minute timer, so it stays silent on failure — a transient
    /// outage must not raise a toast every minute.
    /// </summary>
    public async Task<DirectedNotificationStatus> RefreshDirectedNotificationsAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await CreateDirectedNotificationService().ReadAsync(cancellationToken).ConfigureAwait(false);
        LastDirectedNotificationStatus = outcome.Status;

        if (outcome.Succeeded)
        {
            _directedNotificationFailures = 0;
            ApplyDirectedNotificationFeed(outcome.Feed);
            await FlushPendingReadReceiptsAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (outcome.Status == DirectedNotificationStatus.NotRequested)
        {
            // Signed out (or the token was cleared): drop the previous account's snapshot and its
            // unconfirmed receipts. Logout itself lives in the account partial; owning the reset here
            // keeps both channels' state consistent no matter which path ended the session.
            if (!HasSavedAccountSession) ResetDirectedNotifications();
        }
        else if (IsTransientFailure(outcome.Status))
        {
            _directedNotificationFailures++;
            Log.Debug("定向通知降级：{Status}（连续 {Failures} 次）", outcome.Status, _directedNotificationFailures);
        }
        else if (outcome.Status == DirectedNotificationStatus.Unsupported)
        {
            // The configured client has no directed-notification capability; nothing to retry.
            ResetDirectedNotifications();
        }

        return outcome.Status;
    }

    /// <summary>
    /// Back-off for the polling timer only. An unreachable machine or a backend missing migration 0023
    /// must not be retried every minute forever; the first successful read resets this to zero.
    /// </summary>
    public bool ShouldPollDirectedNotifications => _directedNotificationFailures < 5;

    /// <summary>
    /// Only outcomes that could plausibly change count as failures to back off from.
    /// <c>NotRequested</c> means "we did not ask" (signed out / cancelled) — backing off there would let a
    /// signed-out app stop trying before it ever made a request. <c>Unsupported</c> is a property of the
    /// configured client (e.g. a test double) and will never change by retrying.
    /// </summary>
    private static bool IsTransientFailure(DirectedNotificationStatus status) =>
        status is DirectedNotificationStatus.Unavailable
            or DirectedNotificationStatus.Unauthenticated
            or DirectedNotificationStatus.Failed;

    private void ApplyDirectedNotificationFeed(DirectedNotificationFeed feed)
    {
        // Local optimistic reads win over the server snapshot until the receipt is confirmed: a user who
        // opens a notification while the report is still in flight must not see the dot reappear.
        var items = feed.Items
            .Select(x => _pendingReadReceipts.Contains(x.Id) && x.IsUnread ? x with { ReadAt = DateTime.UtcNow } : x)
            .ToList();
        _directedNotifications = new DirectedNotificationFeed
        {
            Items = items,
            UnreadCount = Math.Max(feed.UnreadCount, items.Count(x => x.IsUnread)),
            HasMore = feed.HasMore
        };
        OnPropertyChanged(nameof(DirectedNotifications));
        OnPropertyChanged(nameof(HasUnreadDirectedNotifications));
    }

    /// <summary>
    /// Marks the given notifications read locally and reports the receipts. Local state flips first so
    /// the UI responds immediately; a failed report is queued and replayed by the next refresh
    /// (CONTRACT-notifications.md §8).
    /// </summary>
    public async Task<bool> MarkDirectedNotificationsReadAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Count == 0) return false;

        var now = DateTime.UtcNow;
        var changed = false;
        var updated = new List<DirectedNotification>(_directedNotifications.Items.Count);
        foreach (var item in _directedNotifications.Items)
        {
            if (item.IsUnread && ids.Contains(item.Id, StringComparer.Ordinal))
            {
                updated.Add(item with { ReadAt = now });
                changed = true;
            }
            else updated.Add(item);
        }

        if (changed)
        {
            _directedNotifications = new DirectedNotificationFeed
            {
                Items = updated,
                UnreadCount = updated.Count(x => x.IsUnread),
                HasMore = _directedNotifications.HasMore
            };
            OnPropertyChanged(nameof(DirectedNotifications));
            OnPropertyChanged(nameof(HasUnreadDirectedNotifications));
        }

        await FlushPendingReadReceiptsAsync(cancellationToken, ids).ConfigureAwait(false);
        return changed;
    }

    private async Task FlushPendingReadReceiptsAsync(CancellationToken cancellationToken, IReadOnlyList<string>? justMarked = null)
    {
        if (justMarked != null)
            foreach (var id in justMarked) _pendingReadReceipts.Add(id);

        if (_pendingReadReceipts.Count == 0 || !HasSavedAccountSession) return;

        // Report the whole queue in one call: the endpoint takes 1..100 ids and is idempotent, so a
        // replay of already-read ids simply comes back marked:0.
        var batch = _pendingReadReceipts.Take(100).ToList();
        if (await CreateDirectedNotificationService().ReportReadAsync(batch, cancellationToken).ConfigureAwait(false))
            foreach (var id in batch) _pendingReadReceipts.Remove(id);
    }

    /// <summary>
    /// Clears the directed-notification snapshot (logout / account switch). The pending queue is dropped
    /// too: those receipts belonged to the previous account and must never be reported for the next one.
    /// </summary>
    private void ResetDirectedNotifications()
    {
        _directedNotifications = DirectedNotificationFeed.Empty;
        _pendingReadReceipts.Clear();
        _directedNotificationFailures = 0;
        LastDirectedNotificationStatus = DirectedNotificationStatus.NotRequested;
        OnPropertyChanged(nameof(DirectedNotifications));
        OnPropertyChanged(nameof(HasUnreadDirectedNotifications));
    }
}
