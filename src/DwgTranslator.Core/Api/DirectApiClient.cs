using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Serilog;

namespace DwgTranslator.Core.Api;

/// <summary>
/// <see cref="IApiClient"/> 的过渡实现：内测阶段的"直连模式"，客户端自己持有 DeepSeek Key。
///
/// 定位（来自重构基线）：
///   · 它存在的唯一理由是后端未就绪时先跑通全部业务流程，切到 Worker 只需要改
///     settings.json 的 apiMode，上层（UI / CAD 插件）只认 IApiClient，不需要改代码；
///   · 翻译能力**完全复用** <see cref="ITranslationService"/>（术语表、格式码恢复、去重、
///     质检、重试都在那条既有管线里），本类不拼 prompt、不 new HttpClient，
///     因此从旧实现切过来时能力不退化；
///   · 账号 / 会员 / 额度 / 设备 / 版本这些**本来属于服务端**的东西直连模式下没有后端，
///     一律明确返回失败，绝不伪造在线状态（假登录会让界面显示"已登录"却拿不到任何额度和设备裁决）。
///
/// 兼容性纪律：不使用 net8 专有 API（Math.Clamp / Index-Range / string.Contains(char) 等），
/// 保证与 Core 的 net48 目标同源可编译。
/// </summary>
public sealed class DirectApiClient : IApiClient
{
    /// <summary>直连模式没有账号服务时统一使用的错误码（含义：上游/后端不可用）。</summary>
    private const string NoBackendErrorCode = "upstream_unavailable";

    /// <summary>给用户看的口径：说明当前为什么没有账号能力，以及怎么切换。</summary>
    private const string NoBackendMessage = "当前为直连模式，未接入账号服务";

    /// <summary>
    /// 整批失败时的兜底文案。这里直接作为 Message 下发，而不是交给
    /// <see cref="ApiErrorMessages.Describe"/>：直连模式整体失败基本都是网络或 Key 问题，
    /// 提示"检查网络与 API Key"比通用口径更可操作。
    /// </summary>
    private const string TranslationBatchFailed = "直连翻译批次整体失败，请检查网络与 API Key 配置";

    private readonly ITranslationService _translationService;
    private readonly IDeepSeekClient _deepSeekClient;

    public DirectApiClient(ITranslationService translationService, IDeepSeekClient deepSeekClient)
    {
        _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        _deepSeekClient = deepSeekClient ?? throw new ArgumentNullException(nameof(deepSeekClient));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="IDeepSeekClient"/> 目前只暴露 ChatCompletionAsync，没有任何"是否已配置 Key"的探针，
    /// 所以这里按"依赖已注入即视为可用"处理（实际上恒为 true）。Key 是否有效只能等第一次调用
    /// 由上游返回 401 才能知道。若将来给 IDeepSeekClient 增加 <c>bool IsConfigured</c>，
    /// 请把这里改为委托给它，避免缺 Key 时界面仍显示"已就绪"。
    /// </remarks>
    public bool IsConfigured => _translationService != null && _deepSeekClient != null;

    /// <inheritdoc />
    public string ModeName => "direct";

    /// <inheritdoc />
    /// <remarks>
    /// 直连模式没有账号后端：这里返回失败而不是伪造成功，令牌一栏保持为空。
    /// 切到 apiMode=worker（<see cref="WorkerApiClient"/>）后同一调用会真正走 POST /v1/auth/login。
    /// </remarks>
    public Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
    {
        // 只记方法名，不记账号与口令。
        Log.Debug("直连模式：{Method} 无账号后端可用", nameof(LoginAsync));
        return Task.FromResult(new LoginResult
        {
            Success = false,
            Token = null,
            ExpiresAt = null,
            ErrorCode = NoBackendErrorCode,
            Message = NoBackendMessage
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// 契约里 ProfileInfo 没有 error_code 字段，直连模式下只能以 null 表示"没有这项数据"；
    /// 切到 worker 模式后即为 GET /v1/profile 的真实结果。
    /// </remarks>
    public Task<ProfileInfo?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：{Method} 无账号后端可用", nameof(GetProfileAsync));
        return Task.FromResult<ProfileInfo?>(null);
    }

    /// <inheritdoc />
    public Task<SubscriptionInfo?> GetSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：{Method} 无账号后端可用", nameof(GetSubscriptionAsync));
        return Task.FromResult<SubscriptionInfo?>(null);
    }

    /// <inheritdoc />
    public Task<UsageInfo?> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：{Method} 无账号后端可用", nameof(GetUsageAsync));
        return Task.FromResult<UsageInfo?>(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 直连模式没有设备裁决这一层：返回失败而不是"绑定成功"。
    /// 汇报 used/max 设备数的真实数值只能来自 Worker（D1 devices 表）。
    /// </remarks>
    public Task<IReadOnlyList<DeviceInfo>?> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：{Method} 无账号后端可用", nameof(GetDevicesAsync));
        return Task.FromResult<IReadOnlyList<DeviceInfo>?>(null);
    }

    public Task<DeviceBindResult> BindDeviceAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：{Method} 无账号后端可用", nameof(BindDeviceAsync));
        return Task.FromResult(new DeviceBindResult
        {
            Success = false,
            UsedDevices = 0,
            MaxDevices = 0,
            ErrorCode = NoBackendErrorCode,
            Message = NoBackendMessage
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// 更新清单由 Pages/KV 托管，直连模式不发起任何网络请求，返回 null 表示"本次没有版本信息"，
    /// 界面不应据此提示"已是最新版本"。切到 worker 模式后才读 update/latest.json。
    /// </remarks>
    public Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：{Method} 无账号后端可用", nameof(RevokeDeviceAsync));
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<CloudGlossaryEntry>?> GetGlossaryAsync(CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：云端术语库不可用");
        return Task.FromResult<IReadOnlyList<CloudGlossaryEntry>?>(null);
    }

    public Task<bool> PutGlossaryAsync(IReadOnlyList<CloudGlossaryEntry> entries, CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：云端术语库不可用");
        return Task.FromResult(false);
    }

    public Task<VersionInfo?> CheckVersionAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        Log.Debug("直连模式：{Method} 无后端可用，跳过版本检查", nameof(CheckVersionAsync));
        return Task.FromResult<VersionInfo?>(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 映射约定：
    ///   · items[].text → TextEntity.PlainText（同时作为 RawText，直连管线没有格式码模板，
    ///     TranslationService.RestoreFormatCodes 在无格式码时原样返回，不会破坏文本）；
    ///   · 关联键用 items 下标生成的 Handle，而不是 item.Id —— 图纸里 id 可能重复，
    ///     下标能保证结果精确回到原来的条目上；id 方面结果按 item.Id 回填；
    ///   · Layer / Context 在 TextEntity 上没有对应字段、直连管线也不使用，
    ///     故不下传（术语、保护规则由本地 GlossaryService / TranslationFilter 承担）；
    ///   · Height 直接透传，Rotation 由"度"换算为 TextEntity 约定的弧度。
    /// </remarks>
    public async Task<TranslationBatchResult> TranslateAsync(TranslationBatchRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var items = request.Items ?? new List<TranslationItem>();
        if (items.Count == 0)
            return new TranslationBatchResult { Success = true };

        var sourceLang = TranslationLanguages.Normalize(request.SourceLang);
        var targetLang = TranslationLanguages.Normalize(request.TargetLang);

        var entities = new List<TextEntity>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null) continue;

            var text = item.Text ?? string.Empty;
            entities.Add(new TextEntity
            {
                // 下标即关联键：保证 1:1 回到 items[i]，不依赖 id 的唯一性。
                Handle = i.ToString(CultureInfo.InvariantCulture),
                PlainText = text,
                RawText = text,
                Height = item.Height,
                Rotation = item.Rotation * Math.PI / 180.0
            });
        }

        List<TranslationPair> pairs;
        try
        {
            // 复用既有翻译管线：去重、术语表、格式码恢复、质检、重试都在 ITranslationService 内，
            // 本类只做 DTO 映射，避免出现第二套 prompt 与第二套重试逻辑。
            pairs = await _translationService
                .TranslateBatchAsync(entities, sourceLang, targetLang, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消语义原样上抛，让上层中止整批任务
        }
        catch (Exception ex)
        {
            // 单条失败已由 TranslationService 内部吞掉并标 TranslationFailed，能抛到这里的是整批级故障。
            Log.Error(ex, "直连翻译批次失败");
            return new TranslationBatchResult
            {
                Success = false,
                ErrorCode = NoBackendErrorCode,
                Message = TranslationBatchFailed
            };
        }

        // Handle 是批内唯一的，可直接建表（用索引赋值而不是 TryAdd：TryAdd 是 netstandard2.1 才有的 API）。
        var byHandle = new Dictionary<string, TranslationPair>(StringComparer.Ordinal);
        foreach (var pair in pairs ?? new List<TranslationPair>())
        {
            if (pair != null) byHandle[pair.Handle] = pair;
        }

        var result = new TranslationBatchResult { Items = new List<TranslationItemResult>(items.Count) };
        int charactersUsed = 0;
        bool anySucceeded = false;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null) continue;

            var text = item.Text ?? string.Empty;
            // 本地估算：按提交的源文本字符数累加（不去重、也不扣除术语命中的部分）。
            // 服务端会以自身统计为准；直连模式下没有服务端，只能给出这个上界口径。
            charactersUsed += text.Length;

            var handle = i.ToString(CultureInfo.InvariantCulture);
            if (!byHandle.TryGetValue(handle, out var pair))
            {
                result.Items.Add(new TranslationItemResult
                {
                    Id = item.Id,
                    TranslatedText = null,
                    FromCache = false,
                    ErrorCode = NoBackendErrorCode
                });
                continue;
            }

            switch (pair.Status)
            {
                case TranslationStatus.Translated:
                case TranslationStatus.GlossaryMatched:
                    result.Items.Add(new TranslationItemResult
                    {
                        Id = item.Id,
                        TranslatedText = string.IsNullOrEmpty(pair.TranslatedText) ? null : pair.TranslatedText,
                        // 直连模式下 FromCache 表示"命中术语表，未调用上游"；一致性缓存的命中情况
                        // 由 TranslationConsistencyService 内部掌握，本层无法观测，故不在此声称命中。
                        FromCache = pair.GlossaryHit,
                        ErrorCode = string.IsNullOrEmpty(pair.TranslatedText) ? "empty_translation" : null
                    });
                    anySucceeded |= !string.IsNullOrEmpty(pair.TranslatedText);
                    break;

                case TranslationStatus.Skipped:
                    result.Items.Add(new TranslationItemResult
                    {
                        Id = item.Id,
                        // 被本地过滤器判定为"不必翻译"（纯数字、符号、已是目标语言等）：
                        // 保留原文，CAD 侧写回就是原样，不该显示成"翻译失败"。
                        TranslatedText = string.IsNullOrEmpty(pair.TranslatedText) ? text : pair.TranslatedText,
                        FromCache = true,
                        ErrorCode = null
                    });
                    anySucceeded = true;
                    break;

                default:
                    // TranslationFailed 等：单条失败不终止整张图纸，把失败原因留给调用方按条处理。
                    Log.Debug("直连翻译失败：handle={Handle} status={Status}", handle, pair.Status);
                    result.Items.Add(new TranslationItemResult
                    {
                        Id = item.Id,
                        TranslatedText = null,
                        FromCache = false,
                        ErrorCode = NoBackendErrorCode
                    });
                    break;
            }
        }

        result.CharactersUsed = charactersUsed;
        // 本地口径：本次未经上游即得到结果的条数（术语命中 + 本地跳过），不是服务端缓存数。
        result.CachedCount = result.Items.Count(r => r.FromCache);

        // 与 Worker 契约一致：部分条目失败不终止整张图纸，全部失败才算批次失败。
        result.Success = anySucceeded;
        if (!anySucceeded)
        {
            result.ErrorCode = NoBackendErrorCode;
            result.Message = TranslationBatchFailed;
            Log.Warning("直连翻译全部失败：{Count} 条", result.Items.Count);
        }

        Log.Debug("直连翻译完成：{Count} 条，字符估算 {Chars}，未调用上游 {Cached} 条",
            result.Items.Count, result.CharactersUsed, result.CachedCount);
        return result;
    }
}

