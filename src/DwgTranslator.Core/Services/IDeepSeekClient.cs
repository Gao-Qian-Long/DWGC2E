namespace DwgTranslator.Core.Services;

/// <summary>
/// Client for calling DeepSeek API (OpenAI-compatible chat completions).
/// </summary>
public interface IDeepSeekClient
{
    /// <summary>Send a chat completion request and return the assistant's response.</summary>
    Task<string> ChatCompletionAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default);
}
