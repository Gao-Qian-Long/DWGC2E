namespace DwgTranslator.Core.Models;

/// <summary>
/// Application configuration loaded from settings.json.
/// </summary>
public class AppConfig
{
    public string DeepSeekApiKey { get; set; } = string.Empty;
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public string SourceLanguage { get; set; } = "ZH";
    public string TargetLanguage { get; set; } = "EN";
    public string GlossaryPath { get; set; } = "glossaries/mechanical_zh_en.json";
    public int BatchSize { get; set; } = 50;
    public int MaxRetryCount { get; set; } = 3;
    public double AutoScaleThreshold { get; set; } = 1.5;
    public double AutoScaleFactor { get; set; } = 0.95;
    public string ExportDirectory { get; set; } = "exports";
    public string LogDirectory { get; set; } = "logs";
}
