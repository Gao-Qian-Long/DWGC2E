using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DwgTranslator.Core.Api;

public sealed partial class WorkerApiClient : ICloudGlossaryClient
{
    private CloudGlossaryState? _legacyGlossaryBasis;

    public async Task<CloudGlossaryState> ReadCloudGlossaryAsync(CancellationToken ct = default)
    {
        var session = RequireGlossarySession();
        var outcome = await SendAsync(HttpMethod.Get, Url("/v1/glossary"), null, DefaultTimeout, ct, expectedSession: session).ConfigureAwait(false);
        return ReadGlossaryOutcome(outcome, session);
    }

    public async Task<CloudGlossaryState> SaveCloudGlossaryAsync(CloudGlossaryState basis, IReadOnlyList<CloudGlossaryEntry> entries, CancellationToken ct = default)
    {
        var session = RequireGlossarySession();
        if (basis == null || !string.Equals(basis.Session, session, StringComparison.Ordinal))
            throw new CloudGlossaryException("session_changed", "账号已变更，请重新读取云端词库；没有上传任何内容。");
        if (entries == null || entries.Count > 1000 || entries.Any(x => !ValidEntry(x)))
            throw new CloudGlossaryException("invalid_entries", "词条格式无效或超过1000条，没有上传任何内容。");
        var payload = new WireGlossaryPutRequest { ExpectedRevision = basis.Revision, Entries = entries.Select(ToWire).ToList() };
        var outcome = await SendAsync(HttpMethod.Put, Url("/v1/glossary"), JsonContent(payload), DefaultTimeout, ct, expectedSession: session).ConfigureAwait(false);
        // Never fetch a newer revision or automatically retry an uncertain write.
        var saved = ReadGlossaryOutcome(outcome, session);
        if (saved.Entries.Count != entries.Count || saved.Entries.Where((x, i) => x.Source != entries[i].Source.Trim() || x.Target != entries[i].Target.Trim()).Any())
            throw new CloudGlossaryException("invalid_response", "云端保存响应与提交内容不符，请下载核对；本机内容仍保留。");
        return saved;
    }

    private string RequireGlossarySession()
    {
        if (!IsConfigured) throw new CloudGlossaryException("unconfigured", "未配置云端地址。");
        var session = GetToken();
        if (string.IsNullOrWhiteSpace(session)) throw new CloudGlossaryException("unauthorized", "请先登录再同步云端词库。");
        return session!;
    }

    private CloudGlossaryState ReadGlossaryOutcome(HttpOutcome outcome, string session)
    {
        if (!string.Equals(session, GetToken(), StringComparison.Ordinal))
            throw new CloudGlossaryException("session_changed", "账号已变更，已忽略旧账号响应。");
        if (!outcome.IsSuccess)
        {
            ThrowIfAuthenticationFailure(outcome);
            if (outcome.StatusCode == 409)
                throw new CloudGlossaryException("glossary_conflict", "云端词库已变化，未覆盖。本机内容仍保留，请使用“比较并合并”核对最新云端内容。");
            throw new CloudGlossaryException(outcome.TransportFailed ? "network_error" : "save_failed",
                outcome.TransportFailed ? "无法确认云端结果，请检查网络。本机内容保留；不要强制覆盖，可下载核对最新状态。" : "云端未接受本次操作，本机内容仍保留，请检查词条格式或重新登录。");
        }
        var wire = Deserialize<WireGlossaryResponse>(outcome.Body);
        if (wire?.Success != true || wire.Entries == null || wire.Revision == null || !Regex.IsMatch(wire.Revision, "\\A[a-f0-9]{64}\\z"))
            throw new CloudGlossaryException("invalid_response", "云端响应不完整，未替换本机内容，也未确认保存成功。");
        var entries = wire.Entries.Select(x => x == null ? null : new CloudGlossaryEntry
        { Id=x.Id, Note=x.Note, Source=x.Source!, Target=x.Target!, Category=x.Category ?? "", Folder=x.Folder ?? "", Enabled=x.Enabled }).ToList();
        if (entries.Any(x => x == null || !ValidEntry(x) || string.IsNullOrWhiteSpace(x.Id)) || entries.Select(x => x!.Id).Distinct().Count() != entries.Count)
            throw new CloudGlossaryException("invalid_response", "云端词条格式异常，未替换本机内容。");
        return new CloudGlossaryState(wire.Revision, entries.Select(x => x!).ToList(), session);
    }

    private static bool ValidEntry(CloudGlossaryEntry x) => x != null
        && !string.IsNullOrWhiteSpace(x.Source) && x.Source.Length <= 500
        && !string.IsNullOrWhiteSpace(x.Target) && x.Target.Length <= 500
        && (x.Category?.Length ?? 0) <= 128 && (x.Folder?.Length ?? 0) <= 128 && (x.Note?.Length ?? 0) <= 1000
        && (x.Id == null || Guid.TryParseExact(x.Id, "D", out _));
    private static WireCloudGlossaryEntry ToWire(CloudGlossaryEntry x) => new()
    { Id=x.Id, Note=x.Note, Source=x.Source, Target=x.Target, Category=x.Category ?? "", Folder=x.Folder ?? "", Enabled=x.Enabled };

    // Retain the old interface without allowing blind overwrites by legacy callers.
    public async Task<IReadOnlyList<CloudGlossaryEntry>?> GetGlossaryAsync(CancellationToken cancellationToken = default)
    {
        _legacyGlossaryBasis = null;
        try { _legacyGlossaryBasis = await ReadCloudGlossaryAsync(cancellationToken).ConfigureAwait(false); return _legacyGlossaryBasis.Entries; }
        catch (CloudGlossaryException) { return null; }
    }
    public async Task<bool> PutGlossaryAsync(IReadOnlyList<CloudGlossaryEntry> entries, CancellationToken cancellationToken = default)
    {
        var basis = _legacyGlossaryBasis;
        if (basis == null) return false;
        try { _legacyGlossaryBasis = await SaveCloudGlossaryAsync(basis, entries, cancellationToken).ConfigureAwait(false); return true; }
        catch (CloudGlossaryException) { return false; }
    }
    private sealed class WireCloudGlossaryEntry
    {
        [JsonPropertyName("id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Id { get; set; }
        [JsonPropertyName("note"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("folder")] public string? Folder { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    }
    private sealed class WireGlossaryPutRequest
    {
        [JsonPropertyName("expected_revision")] public string ExpectedRevision { get; set; } = "";
        [JsonPropertyName("entries")] public List<WireCloudGlossaryEntry> Entries { get; set; } = new();
    }
    private sealed class WireGlossaryResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("revision")] public string? Revision { get; set; }
        [JsonPropertyName("entries")] public List<WireCloudGlossaryEntry>? Entries { get; set; }
    }
}
