using System.IO;

namespace DwgTranslator.Core.Services;

/// <summary>
/// 翻译系统提示词的唯一加载点。
///
/// 以前这段逻辑写死在 MainViewModel 里，任务层（TaskManager）自己建 TranslationService 时
/// 拿不到同一份提示词，只能各写一份——两边一旦不一致，就会出现"单文件翻译"和"批量任务"
/// 质量不同的诡异现象。放到 Core 之后，UI 与任务层共用同一份。
/// </summary>
public static class TranslationPrompt
{
    /// <summary>提示词文件名（沿用既有部署布局：prompts/deepl_context.txt）。</summary>
    public const string FileName = "deepl_context.txt";

    /// <summary>找不到任何提示词文件时的兜底（保证翻译仍能跑）。</summary>
    public const string Fallback =
        "You are a professional engineering translator. Output ONLY the translated text, nothing else.";

    /// <summary>
    /// 查找顺序：数据目录（用户可覆盖）→ 程序目录（随包发布）→ 内置兜底。
    /// </summary>
    /// <param name="appDataDir">%APPDATA%\DwgTranslator 之类的可写数据目录。</param>
    /// <param name="baseDirectory">进程所在目录（AppDomain.CurrentDomain.BaseDirectory）。</param>
    public static string LoadSystemPrompt(string? appDataDir, string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(appDataDir))
        {
            var userPath = Path.Combine(appDataDir, "prompts", FileName);
            if (File.Exists(userPath)) return File.ReadAllText(userPath);
        }

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            var bundledPath = Path.Combine(baseDirectory, "prompts", FileName);
            if (File.Exists(bundledPath)) return File.ReadAllText(bundledPath);
        }

        return Fallback;
    }
}
