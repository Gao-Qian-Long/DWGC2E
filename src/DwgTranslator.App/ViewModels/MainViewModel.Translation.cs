using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using System.IO;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    #region Translation

    /// <summary>
    /// 「开始翻译」。整条流水线（解析 → 提取 → 翻译 → 排版优化 → 写回）都交给任务层，
    /// UI 只订阅它的事件刷新任务表与统计。这里不再 new TranslationService、不再建 HttpClient：
    /// 那正是“UI 直连 AI”的老路，也是这次迁移要拆掉的东西。
    /// </summary>
    [RelayCommand]
    private async Task TranslateAsync()
    {
        await RunTaskQueueAsync(retryFailedFirst: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelTranslate()
    {
        if (!IsTranslating) return;
        CancelTaskQueue();
    }

    [RelayCommand]
    private void CancelExport()
    {
        if (!IsExporting) return;
        IsCancellationRequested = true;
        _exportCts?.Cancel();
        StatusMessage = Strings.Get("StatusCancellingExport");
    }

    /// <summary>
    /// 「重试失败」：任务层把 Failed 的任务重置回等待中并重跑。某个图纸失败从不影响其它图纸，
    /// 所以不再需要旧实现那样把实体状态手工改回 Pending 再整批重译。
    /// </summary>
    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        if (IsProcessing) return;
        await RunTaskQueueAsync(retryFailedFirst: true).ConfigureAwait(true);
    }

    #endregion

    #region Internal Helpers

    private static string NormalizeSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return path.Trim(); }
    }

    #endregion
}