#if DEBUG
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// 迁到任务层时新加的基础设施的行为锁。重点锁两件事：
///
///   1. 配置感知：用户"先启动程序、再在设置页填 Key"是常规流程，客户端必须在<b>调用时</b>
///      读配置，而不是把构造时的 Key 烧死在 HttpClient 里（否则第一次翻译必然 401）。
///   2. 界面层不再自己拼 HTTP：没有 Key / 地址非法时必须当场抛出可读异常，
///      不能让 UI 自己 new HttpClient 去兜底。
/// </summary>
public class DeepSeekClientPlumbingTests : IDisposable
{
    private readonly string _root;

    public DeepSeekClientPlumbingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwgc2e-ai-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private string WriteSettings(string apiKey, string baseUrl = "http://127.0.0.1:1", string model = "deepseek-chat")
    {
        var path = Path.Combine(_root, "settings.json");
        var config = new AppConfig { DeepSeekApiKey = apiKey, DeepSeekBaseUrl = baseUrl, DeepSeekModel = model };
        File.WriteAllText(path, JsonSerializer.Serialize(config, AppConfigJson.WriteOptions));
        return path;
    }

    [Fact]
    public async Task SettingsBacked_WithoutKey_ThrowsReadableError()
    {
        var path = WriteSettings(apiKey: string.Empty);
        using var client = new SettingsBackedDeepSeekClient(path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ChatCompletionAsync("system", "user"));

        Assert.Contains("API Key", ex.Message);
    }

    [Fact]
    public async Task SettingsBacked_IgnoresLegacyProviderKeyFromFile()
    {
        // Provider credentials are Worker-owned. Even debug compatibility code must not
        // revive a legacy client-side key found in settings.json.
        var path = WriteSettings(apiKey: "sk-test-not-a-real-key");
        using var client = new SettingsBackedDeepSeekClient(path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ChatCompletionAsync("system", "user"));
        Assert.Contains("API Key", ex.Message);
    }
    [Fact]
    public async Task SettingsBacked_MissingFile_ThrowsReadableError()
    {
        using var client = new SettingsBackedDeepSeekClient(Path.Combine(_root, "does-not-exist.json"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ChatCompletionAsync("system", "user"));

        Assert.Contains("API Key", ex.Message);
    }

    [Fact]
    public void Factory_WithoutKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeepSeekClientFactory.Create("https://api.deepseek.com", "   ", "deepseek-chat"));
        Assert.Contains("API Key", ex.Message);
    }

    [Fact]
    public void Factory_WithInvalidUrl_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeepSeekClientFactory.Create("这不是地址", "sk-x", "deepseek-chat"));
        Assert.Contains("地址无效", ex.Message);
    }

    [Fact]
    public void Factory_WithValidValues_ReturnsClient()
    {
        var client = DeepSeekClientFactory.Create("https://api.deepseek.com", "sk-x", "deepseek-chat");
        Assert.NotNull(client);
    }

    [Fact]
    public void Prompt_UserCopyWins_ThenFallsBack()
    {
        var userPrompts = Path.Combine(_root, "prompts");
        Directory.CreateDirectory(userPrompts);
        File.WriteAllText(Path.Combine(userPrompts, TranslationPrompt.FileName), "用户自定义提示词");

        Assert.Equal("用户自定义提示词", TranslationPrompt.LoadSystemPrompt(_root, baseDirectory: null));

        // 数据目录没有时用随包目录
        var bundled = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(Path.Combine(bundled, "prompts"));
        File.WriteAllText(Path.Combine(bundled, "prompts", TranslationPrompt.FileName), "随包提示词");
        Assert.Equal("随包提示词", TranslationPrompt.LoadSystemPrompt(bundled, baseDirectory: null));

        // 两处都没有时必须是内置兜底，不能返回空串（空提示词会让模型自由发挥）
        Assert.Equal(TranslationPrompt.Fallback, TranslationPrompt.LoadSystemPrompt(null, null));
    }
}
#endif
