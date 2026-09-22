using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// DeepSeek API client using OpenAI-compatible chat completions endpoint.
/// </summary>
public class DeepSeekClient : IDeepSeekClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public DeepSeekClient(HttpClient httpClient, string model = "deepseek-chat")
    {
        _httpClient = httpClient;
        _model = model;
    }

    public async Task<string> ChatCompletionAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        var request = new ChatCompletionRequest
        {
            Model = _model,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userMessage }
            },
            Temperature = 0.1
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            // 只记规模：错误响应体经常回显请求内容，也就等于回显了图纸文字。
            Log.Debug("DeepSeek API error {StatusCode}: error body {Bytes} bytes", (int)response.StatusCode, errorBody.Length);
            // Sanitize: log status code at Warning level without leaking response internals
            Log.Warning("DeepSeek API error {StatusCode}", (int)response.StatusCode);
            throw new HttpRequestException(
                $"DeepSeek API returned status {(int)response.StatusCode}",
                null, response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: cancellationToken);
        var content = result?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrEmpty(content))
            throw new InvalidOperationException("DeepSeek API returned empty response");

        // 译文属于图纸内容：只记规模，不记文本。
        Log.Debug("DeepSeek response received: {Chars} chars", content.Length);
        return content;
    }

    private class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public ChatMessage[] Messages { get; set; } = Array.Empty<ChatMessage>();

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.1;
    }

    private class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public Choice[]? Choices { get; set; }
    }

    private class Choice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }
}
