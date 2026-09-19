namespace DwgTranslator.Cad.Diagnostics;

/// <summary>
/// 日志脱敏工具：把图纸/实体原文换成"字符数 + 内容指纹"。
///
/// 背景：写回日志会落盘 14 天，而 App 的日志面板提供一键「复制日志 / 导出日志」，
/// 客户排查写回问题时会把整份日志发给支持人员——一行 <c>Handle=原文</c> 就等于把图纸内容外发。
/// 用「字符数 + SHA256 前缀」替代原文后仍然能排查：同一段文字在任何机器、任何进程上都得到
/// 同一枚指纹（SHA256 而不是 GetHashCode，后者跨进程不稳定），既能确认"还是这条没写进去"，
/// 也能配合 Handle 在 CAD 里定位那一条，但拿不到文字内容。
///
/// 单独成文件是为了能被测试直接编译进去（tests/DwgTranslator.Core.Tests 以 Link 方式引用），
/// 所以这里不依赖 CAD API，也不写日志。
/// </summary>
internal static class LogTextRedaction
{
    /// <summary>指纹取 SHA256 前 4 字节（8 位十六进制）：足够区分同一张图纸里的标签，又短到能读。</summary>
    private const int FingerprintBytes = 4;

    /// <summary>把文本描述成可进日志的字符串，例如 <c>"18 chars #3f9a1c07"</c>。</summary>
    public static string Describe(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Length == 0) return "(empty)";
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var digest = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
            var head = new System.Text.StringBuilder(FingerprintBytes * 2);
            for (var index = 0; index < FingerprintBytes; index++) head.Append(digest[index].ToString("x2"));
            return $"{value.Length} chars #{head}";
        }
        catch (System.Exception)
        {
            // 算不出指纹（例如平台限制 SHA256）也绝不退回原文，只报字符数。
            return $"{value.Length} chars";
        }
    }
}
