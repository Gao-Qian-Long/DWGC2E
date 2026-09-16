using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DwgTranslator.Core.Models;
using Serilog;

namespace DwgTranslator.Core.Api;

/// <summary>
/// <see cref="IApiClient"/> 的正式实现：所有请求都走 Cloudflare Worker（端点与报文见
/// docs/CF_BACKEND_CONTRACT.md），DeepSeek Key 只存在于 Worker，客户端不再持有任何模型凭据。
///
/// 为什么要在本文件里另写一套 wire 模型，而不是直接序列化 ApiContracts 里的公开 DTO：
///   · 公开 DTO 的属性名是 PascalCase，而契约 §二 的线上报文是 snake_case；
///   · net8 的 JsonNamingPolicy.SnakeCaseLower 在 net48 上不存在，靠命名策略会让两个目标框架
///     行为不一致（Core 同时多目标 net8.0 与 net48）；
///   · ApiContracts.cs 是冻结的契约文件，不允许为传输层再挂 [JsonPropertyName]。
/// 因此这里定义只用于传输的私有镜像类型，字段名逐字对应契约示例，两种目标框架一致。
///
/// 兼容性纪律：本文件不使用 net8 专有的 HTTP/JSON 便捷 API
/// （HttpClient.GetFromJsonAsync / PostAsJsonAsync / ReadAsStringAsync(CancellationToken) /
/// Math.Clamp / Index-Range / string.Contains(char) 等），一律走
/// HttpRequestMessage + SendAsync + JsonSerializer，保证在 net48 上同样能编译。
/// </summary>
public sealed partial class WorkerApiClient : IApiClient, IAccountSessionClient
{
    /// <summary>翻译请求可能要服务端排队调模型，给足 120 秒。</summary>
    private const int TranslateTimeoutSeconds = 120;

    /// <summary>账号类端点只读写 D1，30 秒足够；再长会让断网时的界面长时间"转圈"。</summary>
    private const int DefaultTimeoutSeconds = 30;

    /// <summary>未配置后端地址时返回的错误码：属于客户端前置校验，不在服务端契约里。</summary>
    private const string UnconfiguredErrorCode = "unconfigured";

    private const string UnconfiguredMessage = "未配置后端地址（apiBaseUrl），请重新安装或联系管理员更新应用配置";

    /// <summary>服务端返回了 2xx 但报文不可解析时使用。</summary>
    private const string BadResponseMessage = "服务端返回了无法解析的响应";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    private static readonly TimeSpan TranslateTimeout = TimeSpan.FromSeconds(TranslateTimeoutSeconds);

    /// <summary>
    /// 契约声明"蛇形/驼峰任一均可，客户端大小写不敏感"：System.Text.Json 的大小写不敏感只忽略
    /// 大小写差异（success / Success），并不会把 charactersUsed 匹配到 characters_used，
    /// 所以 [JsonPropertyName] 一律写成契约 §二 示例里的 snake_case（这也是服务端自检要求的形式）。
    /// </summary>
    private static readonly JsonSerializerOptions WireJsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly Uri? _baseUri;
    private readonly string? _updateManifestUrl;
    private readonly Func<string?> _tokenProvider;
    private readonly string _deviceId;
    private readonly string _deviceName;

    /// <summary>
    /// 正式形态的构造：由 DI 注入已配置好 BaseAddress/超时的 HttpClient。
    /// 更新清单地址留空时按 <c>{baseUrl}/update/latest.json</c> 约定（契约 §五）。
    /// </summary>
    public WorkerApiClient(
        HttpClient httpClient,
        string baseUrl,
        Func<string?> tokenProvider,
        string deviceId,
        string deviceName)
        : this(httpClient, baseUrl, null, tokenProvider, deviceId, deviceName)
    {
    }

    /// <summary>
    /// 带显式更新清单地址的重载：settings.json 里的 <c>UpdateManifestUrl</c> 指向 Pages/KV 时
    /// （默认值 https://cad.pocketter.dpdns.org/update/latest.json）用它，不必再改客户端代码。
    /// </summary>
    public WorkerApiClient(
        HttpClient httpClient,
        string baseUrl,
        string? updateManifestUrl,
        Func<string?> tokenProvider,
        string deviceId,
        string deviceName)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _baseUri = ParseBaseUri(_baseUrl);
        _updateManifestUrl = string.IsNullOrWhiteSpace(updateManifestUrl) ? null : updateManifestUrl!.Trim();
        // 令牌由调用方持有（AppConfig.AuthTokenEncrypted 解密后由 App 层提供），
        // 每次请求现取：重新登录后不需要重建本客户端。
        _tokenProvider = tokenProvider ?? new Func<string?>(() => null);
        _deviceId = deviceId ?? string.Empty;
        _deviceName = deviceName ?? string.Empty;

        if (_baseUri == null && _baseUrl.Length > 0)
            Log.Warning("apiBaseUrl 不是可用的 http/https 绝对地址，Worker 客户端将视为未配置");
    }

    /// <inheritdoc />
    public bool IsConfigured => _baseUri != null;

    /// <inheritdoc />
    public string ModeName => "worker";

    // ────────────────────────────────────────────────────────────────────────
    // 端点映射（与 docs/CF_BACKEND_CONTRACT.md §一 一一对应）
    //   POST /v1/auth/login     登录          POST /v1/translate   批量翻译
    //   GET  /v1/profile        账户信息      POST /v1/devices/bind 设备绑定
    //   GET  /v1/subscription   套餐权益      GET  /v1/version?current=x 版本检查
    //   GET  /v1/usage          用量额度      GET  /update/latest.json    静态更新清单
    // ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new LoginResult { Success = false, ErrorCode = UnconfiguredErrorCode, Message = UnconfiguredMessage };

        if (string.IsNullOrWhiteSpace(_deviceId))
            return new LoginResult { Success = false, ErrorCode = "device_identity_unavailable", Message = "无法保存设备标识，请检查配置目录权限后重启；未占用新设备名额。" };

        var payload = new WireLoginRequest
        {
            Account = account ?? string.Empty,
            Password = password ?? string.Empty,
            DeviceId = _deviceId,
            DeviceName = _deviceName
        };

        // 注意：账号口令只进请求体，绝不写日志（连 Debug 级也不写）。
        var outcome = await SendAsync(HttpMethod.Post, Url("/v1/auth/login"), JsonContent(payload), DefaultTimeout, cancellationToken, includeAuth: false)
            .ConfigureAwait(false);

        if (outcome.TransportFailed)
            return new LoginResult { Success = false, ErrorCode = outcome.TransportErrorCode, Message = ApiErrorMessages.Describe(outcome.TransportErrorCode, outcome.TransportMessage) };

        if (!outcome.IsSuccess)
        {
            var (code, message) = ReadError(outcome);
            Log.Warning("登录失败：HTTP {Status} {ErrorCode}", outcome.StatusCode, code);
            return new LoginResult { Success = false, ErrorCode = code, Message = ApiErrorMessages.Describe(code, message) };
        }

        var response = Deserialize<WireLoginResponse>(outcome.Body);
        if (response == null)
            return new LoginResult { Success = false, ErrorCode = "upstream_unavailable", Message = ApiErrorMessages.Describe("upstream_unavailable", BadResponseMessage) };

        return new LoginResult
        {
            // 服务端没给 success 时用"是否下发了令牌"兜底判断，避免把带 token 的成功响应当成失败。
            Success = response.Success ?? !string.IsNullOrEmpty(response.Token),
            Token = response.Token,
            UserId = response.UserId,
            ExpiresAt = ParseTimestamp(response.ExpiresAt),
            ErrorCode = response.ErrorCode,
            Message = response.ErrorCode == null ? response.Message : ApiErrorMessages.Describe(response.ErrorCode, response.Message)
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 契约里 ProfileInfo / SubscriptionInfo / UsageInfo / VersionInfo 都没有 error_code 字段，
    /// 所以失败只能以 null 表示"这次拿不到"。调用方若需要区分 401 与网络故障，请用
    /// LoginAsync / TranslateAsync 的结果（它们带 error_code），或后续给这些 DTO 补错误字段。
    /// </remarks>
    public async Task<ProfileInfo?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;

        var outcome = await SendAsync(HttpMethod.Get, Url("/v1/profile"), null, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            ThrowIfAuthenticationFailure(outcome);
            LogNonSuccess("GET /v1/profile", outcome);
            return null;
        }

        var wire = Deserialize<WireProfile>(outcome.Body);
        if (wire == null) return null;

        return new ProfileInfo
        {
            DisplayName = wire.DisplayName ?? string.Empty,
            Email = wire.Email ?? string.Empty,
            IsActive = wire.IsActive ?? true
        };
    }

    /// <inheritdoc />
    public async Task<SubscriptionInfo?> GetSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;

        var outcome = await SendAsync(HttpMethod.Get, Url("/v1/subscription"), null, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            ThrowIfAuthenticationFailure(outcome);
            LogNonSuccess("GET /v1/subscription", outcome);
            return null;
        }

        var wire = Deserialize<WireSubscription>(outcome.Body);
        if (wire == null) return null;

        return new SubscriptionInfo
        {
            PlanName = wire.PlanName ?? string.Empty,
            StartsAt = ParseTimestamp(wire.StartsAt),
            ExpiresAt = ParseTimestamp(wire.ExpiresAt),
            AutoRenew = wire.AutoRenew ?? false,
            Entitlements = wire.Entitlements ?? new List<string>()
        };
    }

    /// <inheritdoc />
    public async Task<UsageInfo?> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;

        var outcome = await SendAsync(HttpMethod.Get, Url("/v1/usage"), null, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            ThrowIfAuthenticationFailure(outcome);
            LogNonSuccess("GET /v1/usage", outcome);
            return null;
        }

        var wire = Deserialize<WireUsage>(outcome.Body);
        if (wire == null) return null;

        return new UsageInfo
        {
            MonthlyQuota = wire.MonthlyQuota ?? 0,
            Used = wire.Used ?? 0,
            ResetAt = ParseTimestamp(wire.ResetAt)
        };
    }

    /// <inheritdoc />
    public async Task<TranslationBatchResult> TranslateAsync(TranslationBatchRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (!IsConfigured) return BatchFailure(UnconfiguredErrorCode, UnconfiguredMessage);

        var items = request.Items ?? new List<TranslationItem>();
        var glossary = request.Glossary ?? new List<GlossaryHint>();
        var protection = request.Protection ?? new ProtectionFlags();

        var payload = new WireTranslationRequest
        {
            // 归一化语言码，避免旧的 "zh-cn"/"cn" 写法直接下发（TranslationLanguages 是唯一权威）。
            SourceLang = TranslationLanguages.Normalize(request.SourceLang),
            TargetLang = TranslationLanguages.Normalize(request.TargetLang),
            Protection = new WireProtection
            {
                ProtectDimensions = protection.ProtectDimensions,
                ProtectTolerances = protection.ProtectTolerances,
                ProtectModels = protection.ProtectModels,
                GlossaryFirst = protection.GlossaryFirst
            },
            Glossary = new List<WireGlossaryHint>(glossary.Count),
            Items = new List<WireTranslationItem>(items.Count)
        };

        foreach (var hint in glossary)
        {
            if (hint == null || string.IsNullOrEmpty(hint.Source)) continue;
            payload.Glossary.Add(new WireGlossaryHint { Source = hint.Source, Target = hint.Target ?? string.Empty });
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null) continue;
            payload.Items.Add(new WireTranslationItem
            {
                Id = item.Id,
                Text = item.Text ?? string.Empty,
                Height = item.Height,
                Rotation = item.Rotation,
                Layer = item.Layer ?? string.Empty,
                Context = string.IsNullOrEmpty(item.Context) ? null : item.Context
            });
        }

        // 只记录条数与语言方向：图纸文字内容属于客户资料，不进日志。
        Log.Debug("Worker 翻译请求：{Count} 条 {Src}->{Tgt}", payload.Items.Count, payload.SourceLang, payload.TargetLang);

        var outcome = await SendAsync(HttpMethod.Post, Url("/v1/translate"), JsonContent(payload), TranslateTimeout, cancellationToken, idempotencyKey: request.RequestId)
            .ConfigureAwait(false);

        if (outcome.TransportFailed)
        {
            Log.Warning("翻译请求传输失败：{ErrorCode}", outcome.TransportErrorCode);
            return BatchFailure(outcome.TransportErrorCode!, outcome.TransportMessage);
        }

        if (!outcome.IsSuccess)
        {
            var (code, message) = ReadError(outcome);
            Log.Warning("翻译请求失败：HTTP {Status} {ErrorCode}", outcome.StatusCode, code);
            return BatchFailure(code, message);
        }

        var response = Deserialize<WireTranslationResponse>(outcome.Body);
        if (response == null) return BatchFailure("upstream_unavailable", BadResponseMessage);

        var result = new TranslationBatchResult
        {
            Success = response.Success ?? true,
            // 契约 §七：响应头可带 X-Chars-Used；报文里没给时用响应头对齐计费显示。
            CharactersUsed = response.CharactersUsed ?? ParseCharactersHeader(outcome.CharactersUsedHeader) ?? 0,
            CachedCount = response.CachedCount ?? 0,
            Items = new List<TranslationItemResult>(response.Items != null ? response.Items.Count : 0),
            ErrorCode = response.ErrorCode,
            Message = response.ErrorCode == null ? response.Message : ApiErrorMessages.Describe(response.ErrorCode, response.Message)
        };

        var answeredIds = new HashSet<int>();
        foreach (var wireItem in response.Items ?? new List<WireTranslationItemResult>())
        {
            if (wireItem == null) continue;
            if (!answeredIds.Add(wireItem.Id)) continue; // 重复 id：保留第一条，避免下游按 id 建表时抛异常

            var translated = wireItem.TranslatedText;
            var errorCode = wireItem.ErrorCode;

            if (string.IsNullOrEmpty(errorCode) && string.IsNullOrEmpty(translated))
            {
                // 契约只约定"translated_text 为空且 error_code 有值 = 该条失败"。两者都空时补一个
                // 客户端侧错误码，否则调用方无法区分"这条没翻"和"翻了但结果为空"。
                errorCode = "empty_translation";
            }

            result.Items.Add(new TranslationItemResult
            {
                Id = wireItem.Id,
                TranslatedText = string.IsNullOrEmpty(translated) ? null : translated,
                FromCache = wireItem.FromCache ?? false,
                ErrorCode = errorCode
            });
        }

        // 服务端漏返回的 id 显式标失败：CAD 写回时若是静默保留原文，现场没人知道是哪几条没翻。
        foreach (var sent in payload.Items)
        {
            if (answeredIds.Contains(sent.Id)) continue;
            Log.Debug("翻译响应缺少 id {Id}，标记为 missing_result", sent.Id);
            result.Items.Add(new TranslationItemResult
            {
                Id = sent.Id,
                TranslatedText = null,
                FromCache = false,
                ErrorCode = "missing_result"
            });
        }

        // items 里逐条失败不终止整张图纸（契约 §二），因此 Success 只跟随服务端的批次标志。
        Log.Debug("Worker 翻译完成：成功={Success} 字符={Chars} 缓存={Cached} 条目={Items}",
            result.Success, result.CharactersUsed, result.CachedCount, result.Items.Count);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceInfo>?> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;
        var outcome = await SendAsync(HttpMethod.Get, Url("/v1/devices"), null, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (outcome.TransportFailed || !outcome.IsSuccess)
        {
            LogNonSuccess("GET /v1/devices", outcome);
            return null;
        }
        var wire = Deserialize<WireDevicesResponse>(outcome.Body);
        if (wire?.Devices == null) return null;
        return wire.Devices.Select(d => new DeviceInfo
        {
            DeviceId = d.DeviceId ?? string.Empty,
            DeviceName = d.DeviceName ?? string.Empty,
            Platform = d.Platform ?? "Windows",
            LastSeenAt = ParseTimestamp(d.LastSeen ?? d.LastSeenAt),
            IsCurrent = string.Equals(d.DeviceId, _deviceId, StringComparison.Ordinal)
        }).ToList();
    }

    public async Task<DeviceBindResult> BindDeviceAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new DeviceBindResult { Success = false, ErrorCode = UnconfiguredErrorCode, Message = UnconfiguredMessage };

        var payload = new WireDeviceBindRequest
        {
            // 参数为空时退回构造时注入的设备标识：多数调用场景不关心这两个值，
            // 传空串会把绑定打到空 device_id 上，服务端 §七 的幂等去重也会失效。
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? _deviceId : deviceId,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? _deviceName : deviceName
        };

        var outcome = await SendAsync(HttpMethod.Post, Url("/v1/devices/bind"), JsonContent(payload), DefaultTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.TransportFailed)
            return new DeviceBindResult { Success = false, ErrorCode = outcome.TransportErrorCode, Message = ApiErrorMessages.Describe(outcome.TransportErrorCode, outcome.TransportMessage) };

        if (!outcome.IsSuccess)
        {
            var (code, message) = ReadError(outcome);
            Log.Warning("设备绑定失败：HTTP {Status} {ErrorCode}", outcome.StatusCode, code);
            return new DeviceBindResult { Success = false, ErrorCode = code, Message = ApiErrorMessages.Describe(code, message) };
        }

        var response = Deserialize<WireDeviceBindResponse>(outcome.Body);
        if (response == null)
            return new DeviceBindResult { Success = false, ErrorCode = "upstream_unavailable", Message = ApiErrorMessages.Describe("upstream_unavailable", BadResponseMessage) };

        return new DeviceBindResult
        {
            Success = response.Success ?? true,
            UsedDevices = response.UsedDevices ?? 0,
            MaxDevices = response.MaxDevices ?? 0,
            ErrorCode = response.ErrorCode,
            Message = response.ErrorCode == null ? response.Message : ApiErrorMessages.Describe(response.ErrorCode, response.Message)
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 契约 §五把更新清单放在 Pages/KV（匿名可访问，不是 Worker 端点），所以这里先读清单地址；
    /// 只有没配清单、或清单不可用时，才退回 Worker 的 <c>GET /v1/version?current=x</c>。
    /// 这样即使清单没部署，版本检查也不会静默失效。
    /// </remarks>
    public async Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(deviceId)) return false;
        var outcome = await SendAsync(HttpMethod.Post, Url("/v1/devices/revoke"), JsonContent(new { device_id = deviceId.Trim() }), DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (outcome.TransportFailed || !outcome.IsSuccess)
        {
            Log.Warning("设备撤销失败：HTTP {Status}", outcome.StatusCode);
            return false;
        }
        return true;
    }

    public async Task<VersionInfo?> CheckVersionAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var manifestUri = ResolveManifestUri();
        if (manifestUri != null)
        {
            // 清单可匿名访问：不带 Authorization，免得把令牌送到静态托管域名上。
            var manifest = await SendAsync(HttpMethod.Get, manifestUri, null, DefaultTimeout, cancellationToken, includeAuth: false)
                .ConfigureAwait(false);

            if (manifest.IsSuccess)
            {
                var info = Deserialize<WireVersionInfo>(manifest.Body);
                if (info != null) return MapVersion(info);
            }
            else
            {
                Log.Debug("更新清单不可用（HTTP {Status} / {ErrorCode}），改试 Worker /v1/version",
                    manifest.StatusCode, manifest.TransportErrorCode);
            }
        }

        if (!IsConfigured) return null;

        var path = "/v1/version?current=" + Uri.EscapeDataString(currentVersion ?? string.Empty);
        var outcome = await SendAsync(HttpMethod.Get, Url(path), null, DefaultTimeout, cancellationToken, includeAuth: false).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            LogNonSuccess("GET /v1/version", outcome);
            return null;
        }

        var wire = Deserialize<WireVersionInfo>(outcome.Body);
        return wire == null ? null : MapVersion(wire);
    }

    // ────────────────────────────────────────────────────────────────────────
    // HTTP 层
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单次请求的执行结果。传输层失败（拿不到任何 HTTP 响应）与业务失败（有状态码）分开表达，
    /// 因为前者要映射成 network_error / upstream_unavailable，后者要读响应体里的 error_code。
    /// </summary>
    private sealed class HttpOutcome
    {
        public bool TransportFailed { get; set; }
        public string? TransportErrorCode { get; set; }
        public string? TransportMessage { get; set; }
        public int StatusCode { get; set; }
        public string? Body { get; set; }
        public string? CharactersUsedHeader { get; set; }

        public bool IsSuccess => !TransportFailed && StatusCode >= 200 && StatusCode < 300;
    }

    /// <summary>把业务失败的响应体（error_code + message）解析出来；解析不出时退回状态码映射。</summary>
    private static (string Code, string? Message) ReadError(HttpOutcome outcome)
    {
        if (!string.IsNullOrWhiteSpace(outcome.Body))
        {
            var error = Deserialize<WireErrorBody>(outcome.Body);
            if (error != null && !string.IsNullOrEmpty(error.ErrorCode))
                return (error.ErrorCode!, error.Message);
            if (error != null && !string.IsNullOrEmpty(error.Message))
                return (MapStatusToErrorCode(outcome.StatusCode), error.Message);
        }

        return (MapStatusToErrorCode(outcome.StatusCode), null);
    }

    /// <summary>响应体里没有 error_code 时的兜底映射，依据契约 §三 的状态码表。</summary>
    private static string MapStatusToErrorCode(int statusCode)
    {
        switch (statusCode)
        {
            case 400: return "invalid_request";
            case 401: return "token_expired";
            case 402: return "quota_exceeded";   // 402 也可能是会员到期，报文里的 error_code 优先于这里
            case 403: return "device_limit";
            case 404: return "not_found";
            case 429: return "rate_limited";
        }

        if (statusCode >= 500) return "upstream_unavailable";
        return statusCode > 0 ? "request_failed" : "network_error";
    }

    private static void ThrowIfAuthenticationFailure(HttpOutcome outcome)
    {
        if (outcome.TransportFailed || outcome.StatusCode != 401) return;
        var (code, message) = ReadError(outcome);
        throw new ApiAuthenticationException(code == "request_failed" ? "token_expired" : code, message);
    }

    private static void LogNonSuccess(string endpoint, HttpOutcome outcome)
    {
        if (outcome.TransportFailed)
        {
            Log.Warning("{Endpoint} 传输失败：{ErrorCode}", endpoint, outcome.TransportErrorCode);
            return;
        }

        var (code, _) = ReadError(outcome);
        Log.Debug("{Endpoint} 返回 HTTP {Status} {ErrorCode}", endpoint, outcome.StatusCode, code);
    }

    private static TranslationBatchResult BatchFailure(string errorCode, string? message = null)
        => new TranslationBatchResult
        {
            Success = false,
            ErrorCode = errorCode,
            Message = ApiErrorMessages.Describe(errorCode, message)
        };

    // ────────────────────────────────────────────────────────────────────────
    // 序列化辅助
    // ────────────────────────────────────────────────────────────────────────

    private static HttpContent JsonContent(object payload)
        // StringContent 会产生 Content-Type: application/json; charset=utf-8，与契约一致。
        => new StringContent(JsonSerializer.Serialize(payload, payload.GetType(), WireJsonOptions), Encoding.UTF8, "application/json");

    private static T? Deserialize<T>(string? body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(body!, WireJsonOptions);
        }
        catch (JsonException ex)
        {
            // 不把响应体写进日志：里面可能含用户图纸文字或账号信息。
            Log.Warning("后端响应无法解析为 {WireType}：{Reason}", typeof(T).Name, ex.Message);
            return null;
        }
    }

    /// <summary>服务端按 ISO-8601 UTC 下发（"2026-01-15T00:00:00Z"）；解析失败返回 null 而不是抛异常——
    /// 一个时间格式问题不该让整次调用失败。返回本地时间，界面直接展示即可。</summary>
    private static DateTime? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
            return parsed.ToLocalTime();

        Log.Debug("后端时间字段无法解析：{Value}", value);
        return null;
    }

    private static int? ParseCharactersHeader(string? headerValue)
        => int.TryParse(headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : (int?)null;

    /// <summary>令牌取失败（provider 抛异常）时降级为匿名请求，让服务端用 401 给出统一错误码。</summary>
    private string? GetToken()
    {
        try
        {
            return _tokenProvider();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取访问令牌失败（tokenProvider 抛异常），本次请求按匿名发送");
            return null;
        }
    }

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        var normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring(0, normalized.Length - 3).TrimEnd('/');
        return normalized;
    }
    private static Uri? ParseBaseUri(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        // 只接受 http/https：误填成 C:\path 或 ftp:// 时应尽早判定为"未配置"，
        // 而不是等到发请求才抛 UriFormatException。
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri;
    }

    /// <summary>拼相对路径；<c>_baseUrl</c> 已在构造时去掉尾部斜杠，避免出现双斜杠。</summary>
    private string Url(string path) => _baseUrl + path;

    /// <summary>解析更新清单地址：配置成绝对地址直接用，配置成相对路径则拼到 baseUrl 上，两边都不必改配置。</summary>
    private string? ResolveManifestUri()
    {
        var configured = (_updateManifestUrl ?? string.Empty).Trim();

        if (configured.Length > 0)
        {
            if (Uri.TryCreate(configured, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                return absolute.ToString();

            if (!IsConfigured) return null;
            return _baseUrl + (configured.StartsWith("/", StringComparison.Ordinal) ? configured : "/" + configured);
        }

        return IsConfigured ? _baseUrl + "/update/latest.json" : null;
    }

    private static VersionInfo MapVersion(WireVersionInfo wire) => new VersionInfo
    {
        LatestVersion = wire.LatestVersion ?? string.Empty,
        DownloadUrl = wire.DownloadUrl,
        BackupDownloadUrl = wire.BackupDownloadUrl,
        ReleaseNotes = wire.ReleaseNotes,
        Mandatory = wire.Mandatory ?? false
    };

    // ────────────────────────────────────────────────────────────────────────
    // 私有 wire 模型 —— 字段名与 docs/CF_BACKEND_CONTRACT.md §二 的 JSON 示例逐字对应。
    // 所有字段在响应侧都声明为可空：字段缺失（比如服务端只回 error_code）时不会被读成
    // 0/false 这种"看起来正常"的值，映射时再各自兜底。
    // ────────────────────────────────────────────────────────────────────────

    private sealed class WireProtection
    {
        [JsonPropertyName("protect_dimensions")] public bool ProtectDimensions { get; set; }
        [JsonPropertyName("protect_tolerances")] public bool ProtectTolerances { get; set; }
        [JsonPropertyName("protect_models")] public bool ProtectModels { get; set; }
        [JsonPropertyName("glossary_first")] public bool GlossaryFirst { get; set; }
    }

    private sealed class WireGlossaryHint
    {
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("target")] public string Target { get; set; } = string.Empty;
    }

    private sealed class WireTranslationItem
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
        [JsonPropertyName("height")] public double Height { get; set; }
        [JsonPropertyName("rotation")] public double Rotation { get; set; }
        [JsonPropertyName("layer")] public string Layer { get; set; } = string.Empty;

        /// <summary>块/属性上下文，可为空：空上下文不进报文，服务端按缺省处理。</summary>
        [JsonPropertyName("context")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Context { get; set; }
    }

    private sealed class WireTranslationRequest
    {
        [JsonPropertyName("source_lang")] public string SourceLang { get; set; } = string.Empty;
        [JsonPropertyName("target_lang")] public string TargetLang { get; set; } = string.Empty;
        [JsonPropertyName("protection")] public WireProtection Protection { get; set; } = new WireProtection();
        [JsonPropertyName("glossary")] public List<WireGlossaryHint> Glossary { get; set; } = new List<WireGlossaryHint>();
        [JsonPropertyName("items")] public List<WireTranslationItem> Items { get; set; } = new List<WireTranslationItem>();
    }

    private sealed class WireTranslationItemResult
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("translated_text")] public string? TranslatedText { get; set; }
        [JsonPropertyName("from_cache")] public bool? FromCache { get; set; }
        [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    }

    private sealed class WireTranslationResponse
    {
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("characters_used")] public int? CharactersUsed { get; set; }
        [JsonPropertyName("cached_count")] public int? CachedCount { get; set; }
        [JsonPropertyName("items")] public List<WireTranslationItemResult>? Items { get; set; }
        [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class WireLoginRequest
    {
        [JsonPropertyName("account")] public string Account { get; set; } = string.Empty;
        [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
        [JsonPropertyName("device_id")] public string DeviceId { get; set; } = string.Empty;
        [JsonPropertyName("device_name")] public string DeviceName { get; set; } = string.Empty;
    }

    private sealed class WireLoginResponse
    {
        [JsonPropertyName("user_id")] public string? UserId { get; set; }
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
        [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class WireProfile
    {
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("is_active")] public bool? IsActive { get; set; }
    }

    private sealed class WireSubscription
    {
        [JsonPropertyName("plan_name")] public string? PlanName { get; set; }
        [JsonPropertyName("starts_at")] public string? StartsAt { get; set; }
        [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
        [JsonPropertyName("auto_renew")] public bool? AutoRenew { get; set; }
        [JsonPropertyName("entitlements")] public List<string>? Entitlements { get; set; }
    }

    private sealed class WireUsage
    {
        [JsonPropertyName("monthly_quota")] public long? MonthlyQuota { get; set; }
        [JsonPropertyName("used")] public long? Used { get; set; }
        [JsonPropertyName("reset_at")] public string? ResetAt { get; set; }
    }

    private sealed class WireDevicesResponse
    {
        [JsonPropertyName("devices")] public List<WireDevice>? Devices { get; set; }
    }

    private sealed class WireDevice
    {
        [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
        [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
        [JsonPropertyName("platform")] public string? Platform { get; set; }
        [JsonPropertyName("last_seen")] public string? LastSeen { get; set; }
        [JsonPropertyName("last_seen_at")] public string? LastSeenAt { get; set; }
    }

    private sealed class WireDeviceBindRequest
    {
        [JsonPropertyName("device_id")] public string DeviceId { get; set; } = string.Empty;
        [JsonPropertyName("device_name")] public string DeviceName { get; set; } = string.Empty;
    }

    private sealed class WireDeviceBindResponse
    {
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("used_devices")] public int? UsedDevices { get; set; }
        [JsonPropertyName("max_devices")] public int? MaxDevices { get; set; }
        [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    /// <summary>同时用于 GET /update/latest.json（契约 §五）与 GET /v1/version 的响应。</summary>
    private sealed class WireVersionInfo
    {
        [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("backup_download_url")] public string? BackupDownloadUrl { get; set; }
        [JsonPropertyName("release_notes")] public string? ReleaseNotes { get; set; }
        [JsonPropertyName("mandatory")] public bool? Mandatory { get; set; }
    }

    private sealed class WireErrorBody
    {
        [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}







