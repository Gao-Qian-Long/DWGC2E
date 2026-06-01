using System.Text.Json;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;

var configPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "settings.json"));
if (!File.Exists(configPath))
    configPath = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");

Console.WriteLine($"Loading config from: {configPath}");
var json = File.ReadAllText(configPath);
var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

// Decrypt API key if stored with DPAPI protection
config.DeepSeekApiKey = AppConfig.DecryptApiKey(config.DeepSeekApiKey);

Console.WriteLine($"DeepSeek Model: {config.DeepSeekModel}");
Console.WriteLine($"Base URL: {config.DeepSeekBaseUrl}");
Console.WriteLine($"API Key configured: {!string.IsNullOrEmpty(config.DeepSeekApiKey)}");
Console.WriteLine();

// Load glossary
var glossaryService = new GlossaryService();
if (File.Exists(config.GlossaryPath))
{
    await glossaryService.LoadGlossaryAsync(config.GlossaryPath);
    Console.WriteLine($"Glossary loaded: {glossaryService.GetAllEntries().Count} entries");
}

// Load prompt
var promptPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "prompts", "deepl_context.txt"));
if (!File.Exists(promptPath))
    promptPath = Path.Combine(Directory.GetCurrentDirectory(), "prompts", "deepl_context.txt");
var systemPrompt = File.Exists(promptPath)
    ? File.ReadAllText(promptPath)
    : "You are a professional mechanical engineering translator. Translate the text accurately.";

Console.WriteLine($"System prompt loaded ({systemPrompt.Length} chars)");
Console.WriteLine();

// Create DeepSeek client
using var httpClient = new HttpClient { BaseAddress = new Uri(config.DeepSeekBaseUrl) };
httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.DeepSeekApiKey}");
var deepSeekClient = new DeepSeekClient(httpClient, config.DeepSeekModel);

// Create translation service
var formatCodeParser = new FormatCodeParser();
var translationService = new TranslationService(
    glossaryService, formatCodeParser, deepSeekClient, systemPrompt,
    config.BatchSize, config.MaxRetryCount);

// Test translations
var testTexts = new[]
{
    "轴承座",
    "公差\\P±0.05",
    "法兰连接",
    "密封垫片",
    "液压缸",
    "投料口",
    "搅拌器",
    "管道支架",
};

Console.WriteLine("=== Translation Test ===");
Console.WriteLine();

foreach (var text in testTexts)
{
    try
    {
        var result = await translationService.TranslateAsync(text, config.SourceLanguage, config.TargetLanguage);
        Console.WriteLine($"  {text}");
        Console.WriteLine($"  -> {result}");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {text}");
        Console.WriteLine($"  -> ERROR: {ex.Message}");
        Console.WriteLine();
    }
}

Console.WriteLine("=== Test Complete ===");
