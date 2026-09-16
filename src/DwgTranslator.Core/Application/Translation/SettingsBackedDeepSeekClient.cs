using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DwgTranslator.Core.Models;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// <see cref="IDeepSeekClient"/> 的配置感知实现（Infrastructure 层）：每次调用前重新读一遍
/// settings.json，用**当前**的 BaseUrl / API Key / 模型名 去请求，客户端本身按
/// (BaseUrl, Key, Model) 三元组缓存。
///
/// 存在的理由（这是从 UI 手里收回 HttpClient 时最容易踩的坑）：
/// DI 容器在启动时就把 IDeepSeekClient 注入给了任务层，而真实用户是"先启动程序、再去设置页
/// 填 Key"。如果 Key 在构造时就被烧进 HttpRequestHeaders，第一次翻译必然 401，
/// 且只有重启才恢复。这里把"读配置"推迟到调用时，Key 改完立即生效。
///
/// 同时这也是"UI 不再直连 DeepSeek"的落地点：界面层只拿得到 IDeepSeekClient / IApiClient，
/// 构造 HttpClient、拼 Authorization 头这些事全部留在 Core 内部。
/// </summary>
public sealed class SettingsBackedDeepSeekClient : IDeepSeekClient, IDisposable
{
    private readonly string _settingsPath;
    private readonly object _gate = new();

    private DeepSeekClient? _client;
    private HttpClient? _httpClient;

    /// <summary>当前缓存客户端对应的配置指纹（BaseUrl|Key|Model），变了就重建。</summary>
    private string _signature = string.Empty;

    public SettingsBackedDeepSeekClient(string settingsPath)
    {
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
    }

    /// <inheritdoc />
    public async Task<string> ChatCompletionAsync(
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        var client = ResolveClient();
        return await client.ChatCompletionAsync(systemPrompt, userMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读配置 → 建/复用客户端。只有配置读取失败才抛异常，Key 缺失时给出可操作的中文提示
    /// （调用方通常还会在开始翻译前再挡一次，这里是兜底）。
    /// </summary>
    private DeepSeekClient ResolveClient()
    {
        var config = LoadConfig();
        var baseUrl = string.IsNullOrWhiteSpace(config.DeepSeekBaseUrl)
            ? "https://api.deepseek.com"
            : config.DeepSeekBaseUrl.Trim();
        var apiKey = AppConfig.DecryptApiKey(config.DeepSeekApiKey);
        var model = string.IsNullOrWhiteSpace(config.DeepSeekModel) ? "deepseek-chat" : config.DeepSeekModel.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("尚未配置 API Key，请在「设置」页填写后重试。");

        var signature = $"{baseUrl}|{apiKey}|{model}";

        lock (_gate)
        {
            if (_client != null && string.Equals(_signature, signature, StringComparison.Ordinal))
                return _client;

            // 配置变了：旧连接必须释放，否则每改一次 Key 就泄漏一个 HttpClient。
            _httpClient?.Dispose();

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromMinutes(5)
            };
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            _httpClient = httpClient;
            _client = new DeepSeekClient(httpClient, model);

            var changed = !string.IsNullOrEmpty(_signature);
            _signature = signature;
            Log.Information("DeepSeek 客户端已{Action}（BaseUrl={BaseUrl}，模型={Model}）",
                changed ? "随配置更新" : "初始化", baseUrl, model);

            return _client;
        }
    }

    private AppConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AppConfig();
            var json = File.ReadAllText(_settingsPath);
            if (string.IsNullOrWhiteSpace(json)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.ReadOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取 AI 配置失败，改用默认配置：{Path}", _settingsPath);
            return new AppConfig();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _client = null;
            _signature = string.Empty;
        }
    }
}
