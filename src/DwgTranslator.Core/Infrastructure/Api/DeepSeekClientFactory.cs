using System;
using System.Net.Http;

namespace DwgTranslator.Core.Services;

/// <summary>
/// 创建 <see cref="IDeepSeekClient"/> 的唯一入口（Infrastructure）。
///
/// 界面层从此不再出现 <c>new HttpClient()</c> 与 <c>DefaultRequestHeaders.Add("Authorization", ...)</c>：
///   · 任务层 / 翻译管线用 <see cref="SettingsBackedDeepSeekClient"/>（每次调用前重读配置）；
///   · 设置页"测试连接"用 <see cref="Create"/>（直接测用户此刻输入在表单里的 BaseUrl / Key / 模型，
///     因为那些值还没落盘，不能走"读配置"的客户端）。
/// 两处共用同一份地址与鉴权拼装逻辑，避免出现"测试连接能过、正式翻译 401"这类双份实现不一致。
/// </summary>
public static class DeepSeekClientFactory
{
    public const string DefaultBaseUrl = "https://api.deepseek.com";
    public const string DefaultModel = "deepseek-chat";

    /// <summary>
    /// 建一个直连客户端。地址与 Key 会在使用前校验，非法值在这里就抛出可读异常，
    /// 而不是等第一次请求返回 401 才暴露。
    /// </summary>
    public static IDeepSeekClient Create(string? baseUrl, string? apiKey, string? model, TimeSpan? timeout = null)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        var key = apiKey?.Trim() ?? string.Empty;
        var modelName = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("尚未配置 API Key。");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"API 地址无效：{url}");

        var httpClient = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");

        return new DeepSeekClient(httpClient, modelName);
    }
}
