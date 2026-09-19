using DwgTranslator.Cad.Diagnostics;
namespace DwgTranslator.Core.Tests;

/// <summary>
/// 日志脱敏：写回日志会落盘 14 天，且 App 提供一键「导出日志」，客户排查时会把它发给支持人员。
/// 这几条测试锁住"原文永远不出现在日志描述里，但同一段文字仍能被认出来"。
/// </summary>
public class LogTextRedactionTests
{
    [Fact]
    public void RedactedDescriptionNeverCarriesTheSourceText()
    {
        const string label = "阀门反馈 DN100";
        var description = LogTextRedaction.Describe(label);
        Assert.DoesNotContain(label, description);
        Assert.DoesNotContain("阀门", description);
        Assert.StartsWith($"{label.Length} chars #", description);
    }

    [Fact]
    public void SameTextAlwaysProducesTheSameFingerprint()
    {
        // 排查时要能确认"还是同一条标签没写进去"，所以指纹必须稳定。
        Assert.Equal(LogTextRedaction.Describe("阀门反馈 DN100"), LogTextRedaction.Describe("阀门反馈 DN100"));
    }

    [Fact]
    public void FingerprintIsStableAcrossProcessesNotJustWithinOne()
    {
        // 固定期望值 = 锁住算法（SHA256 前 4 字节）。谁把它换成 string.GetHashCode()——
        // 那东西每个进程都不一样，日志里的指纹就再也对不上了——这里会立刻红。
        Assert.Equal("20 chars #410a2f06", LogTextRedaction.Describe("VALVE-FEEDBACK-DN100"));
    }

    [Fact]
    public void DifferentTextProducesDifferentFingerprints()
    {
        Assert.NotEqual(LogTextRedaction.Describe("阀门反馈 DN100"), LogTextRedaction.Describe("阀门反馈 DN150"));
        // 只有尾部空格不同也必须区分：排版冲突经常就差在一个尾随空格上。
        Assert.NotEqual(LogTextRedaction.Describe("轴"), LogTextRedaction.Describe("轴 "));
    }

    [Fact]
    public void EmptyTextIsReportedWithoutThrowing()
    {
        Assert.Equal("(empty)", LogTextRedaction.Describe(null));
        Assert.Equal("(empty)", LogTextRedaction.Describe(string.Empty));
    }
}
