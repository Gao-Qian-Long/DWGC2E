using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.App.Views.Controls;
using DwgTranslator.App.Views.Pages;

namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyRefinementAsync(MainWindow window, MainViewModel vm)
    {
        var previous = vm.CurrentPage;
        var root = (FrameworkElement)window.Content;
        var host = (FrameworkElement)window.FindName("PageHost");
        Check((double)Resources["FontSize.Body"] == 14 && (double)Resources["FontSize.Table"] == 14, "refinement body and table tokens 14 DIP");
        // §自适应（2026-09-25 用户批注「用鼠标把界面横向一直缩小，界面没有任何的自适应调节」）：
        // 常规显示器上的最小支持宽度是 1024 DIP（MainWindow.xaml 的 MinWidth），它落在**图标栏**那一段
        // （窗口 ≤1358 收侧栏），所以最小窗口 = 图标侧栏 64 + 页面可用宽 928。
        // 关键：这一档必须靠"页面自己重排"成立，而不是靠内容被裁掉——下面 size.Width == 1024 的窄档断言
        // 逐页检查两栏堆叠 / 表格收列 / 页面滚动是否真的生效（用户看到的问题正是这些没生效）。
        // 同时尝试更窄宽度时 WPF 必须把窗口约束在最小宽度，避免内容横向裁切。
        // The preceding 1366×960 native toast probe can trigger Windows snap/maximize
        // on a 960-DIP monitor. Width/Height setters do not resize a maximized window.
        window.WindowState = WindowState.Normal;
        var workAreaWidth = SystemParameters.WorkArea.Width;
        var minimumLayoutWidth = Math.Min(1024, Math.Max(800, workAreaWidth - 20));
        FitNativeWindow(window, new Size(minimumLayoutWidth, 640));
        await WaitForStableAsync(() => window.ActualWidth, $"window width {minimumLayoutWidth:F0}x640");
        await WaitForStableAsync(() => host.ActualWidth, $"page canvas {minimumLayoutWidth:F0}x640");
        var sidebarProbe = (System.Windows.Controls.ColumnDefinition)window.FindName("SidebarColumn");
        Console.WriteLine($"INFO minimum-window probe: actual={window.ActualWidth:F1}x{window.ActualHeight:F1} min={window.MinWidth:F1}x{window.MinHeight:F1} content={((FrameworkElement)window.Content).ActualWidth:F1} sidebar={sidebarProbe.Width.Value:F1} host={host.ActualWidth:F1} pageMargin={window.Resources["Spacing.Page"]}");
        Check(ResponsiveLayout.GetIsCompact(window) && window.MinWidth <= 1024 && window.MinHeight <= 640,
            $"the supported minimum window uses the icon rail (min={window.MinWidth}x{window.MinHeight}, actual={window.ActualWidth:F1}x{window.ActualHeight:F1}, content={((FrameworkElement)window.Content).ActualWidth:F1}, host={host.ActualWidth:F1}, sidebar={sidebarProbe.Width.Value:F1}, compact={ResponsiveLayout.GetIsCompact(window)})");
        Check(Math.Abs(sidebarProbe.Width.Value - 64) < 0.1,
            $"the supported minimum collapses the sidebar to the 64 DIP icon rail (sidebar={sidebarProbe.Width.Value:F1})");
        window.Width = Math.Max(640, window.MinWidth - 100);
        await WaitForStableAsync(() => window.ActualWidth, "window minimum-width resize constraint");
        Check(window.ActualWidth + 1 >= window.MinWidth,
            $"resizing narrower than the minimum cannot clip the window content (requested={window.MinWidth - 100:F1}, actual={window.ActualWidth:F1}, min={window.MinWidth:F1})");
        vm.CurrentPage = MainViewModel.PageSettings;
        await NavIdleAsync();
        var minimumSettings = FindVisual<SettingsPage>(window);
        Check(((FrameworkElement)minimumSettings.FindName("CompactSections")).IsVisible
              && !((FrameworkElement)minimumSettings.FindName("SettingsNavCard")).IsVisible,
            "the icon-rail minimum window exposes the section picker instead of the nav column");
        vm.CurrentPage = "translate";
        await NavIdleAsync();
        // 宽档对齐（"界面显示要一致"）：离开图标栏那一段后必须恢复展开侧栏。
        if (FitsNativeWindow(window, new Size(1440, 900)))
        {
            FitNativeWindow(window, new Size(1440, 900));
            await WaitForStableAsync(() => window.ActualWidth, "window width 1440x900");
            Check(!ResponsiveLayout.GetIsCompact(window), "leaving the compact band restores the full sidebar");
        }
        else
        {
            Console.WriteLine($"SKIP 1440x900 native probe: work area is {SystemParameters.WorkArea.Width:F1}x{SystemParameters.WorkArea.Height:F1} DIP");
        }
        // §自适应 迟滞行为：从图标栏那一段往上拖时，窗口落在 Enter(1358)..Leave(1374) 之间必须**保持**
        // 图标栏，越过 Leave 才展开——否则侧栏会在断点上反复收放，页面跟着抖。
        // 这一段只能靠"先窄后宽"构造出来（从宽档往下拖时，1358 就已经收起了）。
        FitNativeWindow(window, new Size(1024, 720)); await WaitForStableAsync(() => window.ActualWidth, "hysteresis entry 1024");
        if (FitsNativeWindow(window, new Size(1380, 720)))
        {
            FitNativeWindow(window, new Size(1366, 720)); await WaitForStableAsync(() => window.ActualWidth, "hysteresis hold 1366");
            Check(ResponsiveLayout.GetIsCompact(window),
                "the icon rail holds inside the hysteresis band while growing from the narrow side");
            FitNativeWindow(window, new Size(1380, 720)); await WaitForStableAsync(() => window.ActualWidth, "hysteresis exit 1380");
            Check(!ResponsiveLayout.GetIsCompact(window), "passing the hysteresis ceiling restores the full sidebar");
        }
        else Console.WriteLine("SKIP hysteresis native probe: 1380x720 DIP does not fit the monitor work area");
        var nativeLayoutSizes = workAreaWidth >= 1024
            ? new[] { new Size(1024,720), new Size(1120,720), new Size(1366,768), new Size(1440,900), new Size(1920,1080) }
            : Array.Empty<Size>();
        foreach (var size in nativeLayoutSizes.Where(size => FitsNativeWindow(window, size)))
        {
            FitNativeWindow(window, size);
            // 窗口尺寸由 OS/合成器异步生效：必须等客户区真正稳定后再量，否则会读到旧尺寸。
            await WaitForStableAsync(() => window.ActualWidth, $"window width {size.Width}x{size.Height}");
            await WaitForStableAsync(() => host.ActualWidth, $"page canvas {size.Width}x{size.Height}");
            // 图标栏一直用到 1358：1024/1120 走图标栏，1366 及以上走展开栏（与 MainWindow.xaml.cs 对齐）。
            var expectedCompact = size.Width <= 1358;
            if (ResponsiveLayout.GetIsCompact(window) != expectedCompact && size.Width > 1358)
            {
                // 迟滞：从图标栏那一段走到 1366（仍在 Leave=1374 之内）会保持图标栏。
                // 矩阵要量的是"稳定态"，所以先拉宽到 1600 越过迟滞带，再回到目标宽度。
                FitNativeWindow(window, new Size(Math.Min(Math.Max(size.Width, 1600), NativeWorkAreaLimit(window).Width), size.Height));
                await WaitForStableAsync(() => window.ActualWidth, "hysteresis reset 1600");
                FitNativeWindow(window, size);
                await WaitForStableAsync(() => window.ActualWidth, $"window width {size.Width}x{size.Height}");
            }
            Check(ResponsiveLayout.GetIsCompact(window) == expectedCompact, "sidebar breakpoint " + size);
            Check(host.ActualWidth <= size.Width - (expectedCompact ? 64 : 200) + 1, $"no oversized minimum page canvas {size} (host={host.ActualWidth:F1}, window={window.ActualWidth:F1}, page={vm.CurrentPage})");
            foreach (var page in new[] { "translate", "batch", "glossary", "account", "settings" })
            {
                vm.CurrentPage = page;
                await NavIdleAsync();
                var header = FindVisuals<PageHeader>(window).Single(x => x.IsVisible);
                Check(header.TranslatePoint(new Point(), root).X >= 64, "header remains within content " + page + size);
                Check(header.TranslatePoint(new Point(header.ActualWidth,0), root).X <= root.ActualWidth + 1, "header not clipped horizontally " + page + size);
                if (page == "translate")
                {
                    var translate = FindVisual<TranslatePage>(window);
                    var summary = (FrameworkElement)translate.FindName("TranslationSummaryBar");
                    var metrics = (FrameworkElement)translate.FindName("TranslationSummaryMetrics");
                    var nextStep = (TextBlock)translate.FindName("WorkspaceNextStepTextBlock");
                    var summaryRight = summary.TranslatePoint(new Point(summary.ActualWidth, 0), root).X;
                    Check(summary.IsVisible && summary.ActualHeight >= 64 && summaryRight <= root.ActualWidth + 1,
                        "translation summary remains fully visible " + size);
                    if (size.Width == 1024)
                    {
                        // §自适应（2026-09-25）：最小窗口必须真的重排——两栏折成上下 + 整块可纵向滚动，
                        // 而不是把右栏裁出视口。这正是用户报的"界面没有任何的自适应调节"。
                        var settingsColumn = (FrameworkElement)translate.FindName("TranslationSettingsScroll");
                        var workspace = (FrameworkElement)translate.FindName("TranslationWorkspace");
                        var queueSurface = (FrameworkElement)translate.FindName("QueueSurface");
                        Check(Grid.GetRow(settingsColumn) == 1 && Grid.GetColumnSpan(settingsColumn) == 2,
                            $"the minimum window stacks the settings column under the queue (row={Grid.GetRow(settingsColumn)}, span={Grid.GetColumnSpan(settingsColumn)})");
                        Check(Grid.GetColumnSpan(queueSurface) == 2
                              && queueSurface.ActualWidth >= workspace.ActualWidth - 24,
                            $"the stacked queue occupies the full workspace instead of a clipped half-page (queue={queueSurface.ActualWidth:F1}, workspace={workspace.ActualWidth:F1})");
                        Check(((ScrollViewer)translate.FindName("WorkspaceScroll")).VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                            "the stacked workspace becomes scrollable so the settings cards stay reachable");
                        var narrowQueue = (DataGrid)translate.FindName("DrawingQueue");
                        Check(narrowQueue.Columns.Single(c => Equals(c.Header, "状态与进度")).Visibility == Visibility.Collapsed
                              && narrowQueue.Columns.Single(c => Equals(c.Header, "状态")).Visibility == Visibility.Visible,
                            "the stacked queue keeps its full column set (it owns the whole page width)");
                        var narrowAction = narrowQueue.Columns.Single(c => Equals(c.Header, "操作")).ActualWidth;
                        Check(Math.Abs(narrowAction - 172) < 0.6,
                            $"the queue action column keeps its declared width at the minimum window (actual={narrowAction:F1})");
                        var queueTail = narrowQueue.Columns.Single(c => Equals(c.Header, ""));
                        var queueActionHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(narrowQueue)
                            .Single(c => Equals(c.Content, "操作"));
                        var queueRight = queueActionHeader.TranslatePoint(new Point(queueActionHeader.ActualWidth, 0), narrowQueue).X;
                        Check(queueTail.Visibility == Visibility.Collapsed && narrowQueue.ActualWidth - queueRight <= 18,
                            $"minimum queue allocates spare width to content, not an empty tail (gap={narrowQueue.ActualWidth - queueRight:F1})");
                    }
                    var metricLabels = FindVisuals<TextBlock>(metrics).Where(x => x.IsVisible).Select(x => x.Text).ToHashSet();
                    // "待校对"改叫"未校对"：校对是可选的（2026-09-22 用户批注「校对不是必须的」），
                    // 它不再是流程上的一道门，所以标签必须改成描述性的，而不是"待办"的语气。
                    Check(new[] { "图纸", "运行状态", "整体进度", "翻译成功", "失败", "未校对", "待导出" }.All(metricLabels.Contains),
                        "translation summary exposes all seven workflow metrics " + size);
                    Check(nextStep.IsVisible && nextStep.ActualWidth >= 80,
                        "translation next-step guidance remains visible " + size);

                    if (size.Width == 1024 && size.Height == 720)
                    {
                        var chooseFile = (Button)translate.FindName("EmptySelectFileButton");
                        var bottom = chooseFile.TranslatePoint(new Point(0, chooseFile.ActualHeight), root).Y;
                        Check(chooseFile.IsVisible && chooseFile.ActualHeight >= 30 && bottom <= root.ActualHeight + 1,
                            "minimum window keeps primary file CTA fully visible even with the stacked workspace");
                        var rows = CreateMinimumTableRows();
                        try
                        {
                            foreach (var row in rows) vm.DrawingFiles.Add(row);
                            await NavIdleAsync();
                            Capture(window, "minimum-translate-with-rows");
                            var output = System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("DWGC2E_UI_SMOKE_OUTPUT") ?? "artifacts/ui-smoke");
                            CapturePhysicalWindow(window, System.IO.Path.Combine(output, "physical-minimum-translate-with-rows.png"));
                        }
                        finally { foreach (var row in rows) vm.DrawingFiles.Remove(row); }
                    }

                    if (size.Width is 1366 or 1440)
                    {
                        var failed = new DrawingFileItem(System.IO.Path.Combine(AppDataDir, $"refinement-failed-{size.Width}.dwg"));
                        failed.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(failed.FullPath)
                        {
                            Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.Failed,
                            Error = "受控失败夹具",
                            Progress = 35,
                            TextCount = 12
                        });
                        var review = new DrawingFileItem(System.IO.Path.Combine(AppDataDir, $"refinement-review-{size.Width}.dwg"));
                        review.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(review.FullPath)
                        {
                            Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.ReadyForReview,
                            Progress = 100,
                            TextCount = 18
                        });
                        var completed = new DrawingFileItem(System.IO.Path.Combine(AppDataDir, $"refinement-export-{size.Width}.dwg"));
                        completed.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(completed.FullPath)
                        {
                            Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed,
                            Progress = 100,
                            TextCount = 21
                        });
                        var fixtureRows = new[] { failed, review, completed };
                        try
                        {
                            foreach (var row in fixtureRows) vm.DrawingFiles.Add(row);
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            translate.UpdateLayout();

                            var queue = (DataGrid)translate.FindName("DrawingQueue");
                            queue.ScrollIntoView(failed);
                            queue.UpdateLayout();
                            Check(ScrollViewer.GetHorizontalScrollBarVisibility(queue) == ScrollBarVisibility.Disabled,
                                "translation queue does not rely on horizontal scrolling " + size);
                            var exportColumn = queue.Columns.Single(c => Equals(c.Header, "导出"));
                            Check(exportColumn is DataGridTemplateColumn && exportColumn.ActualWidth >= 59.5,
                                $"translation export selector matches the glossary template and keeps its full header {size}");
                            var exportSelector = FindVisuals<CheckBox>(queue).FirstOrDefault(c =>
                                AutomationProperties.GetName(c) == "选择导出图纸");
                            Check(exportSelector != null && exportSelector.ActualWidth > 0 && exportSelector.ActualHeight > 0,
                                $"translation export checkbox is fully rendered {size}");
                            var actionHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(queue)
                                .Single(x => Equals(x.Content, "操作"));
                            var actionRight = actionHeader.TranslatePoint(new Point(actionHeader.ActualWidth, 0), queue).X;
                            // 列宽预算取证：文件名改成固定宽之后，"操作"表头右缘必须仍在表宽内。
                            Console.WriteLine($"QUEUE_WIDTH {size} queue={queue.ActualWidth:F1} actionRight={actionRight:F1}");
                            Check(actionHeader.IsVisible && actionRight <= queue.ActualWidth + 1,
                                "translation action column is not clipped " + size);
                            // 「表头不出界」不等于"按钮没被压窄"：列宽合计超出可用宽时，表格会按比例压缩
                            // **所有**列。文件名改成固定宽之后（用户批注 2026-09-22），这条直接量按钮与单元格的
                            // 关系，防止把「移除」压掉。窗口下限抬到 1366 之后队列在最窄窗口也有 707 DIP
                            // （≥ 固定列 680），旧的"1280 下 601 DIP 会压掉第三个按钮"的窄窗豁免不再成立：
                            // 矩阵里每一档都必须三按钮齐全。
                            if (size.Width >= 1366)
                            {
                                var widestActionCell = FindVisuals<System.Windows.Controls.DataGridCell>(queue)
                                    .Select(cell => new { Cell = cell, Buttons = FindVisuals<Button>(cell).Where(b => b.IsVisible).ToArray() })
                                    .Where(x => x.Buttons.Length > 0)
                                    .OrderByDescending(x => x.Buttons.Length)
                                    .First();
                                Check(widestActionCell.Buttons.Length == 3,
                                    $"export-ready queue row exposes export, proofread and remove {size}");
                                var cellRight = widestActionCell.Buttons
                                    .Select(b => b.TransformToAncestor(widestActionCell.Cell)
                                        .TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight)).Right).Max();
                                // 容差 2 DIP：「操作」是最后一个实列、后面只有空白的收尾星号列，而 DataGridCell
                                // 默认 ClipToBounds=False —— 溢出 1~2 DIP 只会压到那条空白列，不会被裁掉，
                                // 因此这里拦的是"被压窄到明显放不下"（改前会溢出十几 DIP），不是像素级对齐。
                                Check(cellRight <= widestActionCell.Cell.ActualWidth + 2,
                                    $"queue row actions fit inside their cell {size} (right={cellRight:F1}, cell={widestActionCell.Cell.ActualWidth:F1})");
                            }
                            // 未校对（ReadyForReview）与已校对（Completed）现在都是"待导出"：两者都能立即导出。
                            Check(review.WorkflowStatusText == "待导出" && completed.WorkflowStatusText == "待导出",
                                "translated rows read as export-ready whether or not they were proofread " + size);
                            // 七格互斥（2026-09-23）：未校对 = 已翻译·未校对·未导出（fixture 里是 review 行），
                            // 待导出 = 已校对·未导出（completed 行）。两者不再重叠，导出后同时清零。
                            Check(vm.WorkspaceReviewCount == 1 && vm.WorkspacePendingExportCount == 1 && vm.WorkspaceFailedCount == 1,
                                "translation summary follows drawing collection changes " + size);
                            Check(vm.WorkspaceExportableCount == 2,
                                "export capability still covers both unproofread and proofread rows " + size);

                            var export = (Button)translate.FindName("ExportSelectionButton");
                            var retry = (Button)translate.FindName("RetryFailedButton");
                            // 关键回归：只翻译、没校对的行必须已经能导出（旧实现这里断言的是"不能导出"）。
                            Check(export.IsEnabled && vm.CanExportWorkspace,
                                "a translated-but-unproofread drawing enables export without proofreading " + size);
                            completed.IsIncludedForExport = false;
                            review.IsIncludedForExport = false;
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            Check(retry.IsEnabled && vm.CanRetryFailedDrawingTasks,
                                "failed drawings enable the dedicated retry action " + size);
                            Check(!vm.CanStartWorkspaceTranslation,
                                "failed drawings do not incorrectly enable the start-translation action " + size);
                            // 另一半语义不能被这次解耦带走：导出仍然只看当前勾选的行。
                            Check(!export.IsEnabled && !vm.CanExportWorkspace,
                                "export stays disabled while no exportable row is selected " + size);
                            review.IsIncludedForExport = true;
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            Check(export.IsEnabled && vm.CanExportWorkspace,
                                "export follows the per-drawing selection " + size);

                            var actionBar = (FrameworkElement)translate.FindName("TranslationActionBar");
                            var start = FindVisuals<Button>(translate).Single(x => x.IsVisible && Equals(x.Content, "开始翻译"));
                            foreach (var button in new[] { export, retry, start })
                            {
                                var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                                Check(button.IsVisible && button.ActualWidth >= 100 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
                                    $"translation primary action remains reachable ({button.Content}) {size}");
                            }
                            Check(actionBar.ActualHeight >= 40, "translation sticky action bar remains usable " + size);
                            Capture(window, $"translation-workflow-{size.Width}-{size.Height}");
                        }
                        finally
                        {
                            foreach (var row in fixtureRows) vm.DrawingFiles.Remove(row);
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                    }
                }
                if (page == "glossary")
                {
                    var glossary = FindVisual<GlossaryPage>(window);
                    var selectionToolbar = (FrameworkElement)glossary.FindName("SelectionToolbar");
                    selectionToolbar.UpdateLayout();
                    var clearSelectionButton = FindVisuals<Button>(selectionToolbar).Single(b => Equals(b.Content, "取消选择"));
                    var clearSelectionBounds = clearSelectionButton.TransformToAncestor(selectionToolbar)
                        .TransformBounds(new Rect(0, 0, clearSelectionButton.ActualWidth, clearSelectionButton.ActualHeight));
                    Check(clearSelectionBounds.Left >= -1 && clearSelectionBounds.Right <= selectionToolbar.ActualWidth + 1,
                        "glossary selection actions wrap without clipping the clear-selection button " + size);
                    var grid = (DataGrid)glossary.FindName("TermList");
                    Check(grid.ActualHeight >= 40, "glossary retains usable table viewport " + size);
                    grid.UpdateLayout();
                    var categoryColumn = grid.Columns.Single(c => Equals(c.Header, "分类"));
                    var actionColumn = grid.Columns.Single(c => Equals(c.Header, "操作"));
                    Check(categoryColumn.ActualWidth >= 143.5, "glossary category column remains readable " + size);
                    Check(actionColumn.ActualWidth >= 149.5, "glossary action column remains usable " + size);
                    var actionHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).Single(h => Equals(h.Content, "操作"));
                    var actionRight = actionHeader.TranslatePoint(new Point(actionHeader.ActualWidth, 0), grid).X;
                    Check(actionHeader.IsVisible && actionRight <= grid.ActualWidth + 1, "glossary action column is not clipped " + size);
                    // §自适应：这张表刻意不做窄档收列（「分类」「启用」是行内可交互控件，收进只读的合并列
                    // 等于在窄档拿走编辑/启用能力），它用"列宽合计不超过视口"来证明没有裁切：列都带 MinWidth，
                    // 一旦放不下就会出横向滚动条（本表是唯一保留 Auto 兜底的表格），合计必然超过视口。
                    var visibleColumns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToArray();
                    var declaredColumns = visibleColumns.Sum(c => c.ActualWidth);
                    Check(declaredColumns <= grid.ActualWidth + 1,
                        $"glossary columns fit the viewport without clipping (declared={declaredColumns:F1}, table={grid.ActualWidth:F1}) {size}");
                    if (grid.Items.Count > 0)
                    {
                        grid.ScrollIntoView(grid.Items[0]);
                        grid.UpdateLayout();
                        var categoryEditor = FindVisuals<ComboBox>(grid).FirstOrDefault(c => c.IsVisible && c.DataContext is DwgTranslator.Core.Models.GlossaryEntry);
                        var categoryEditorWidth = categoryEditor?.ActualWidth ?? 0;
                        Check(categoryEditorWidth >= 123.5, $"glossary category editor has readable content width {size}; actual={categoryEditorWidth:F1}");
                        var rowButtons = FindVisuals<Button>(grid).Where(b => b.IsVisible).ToList();
                        var primaryAction = rowButtons.FirstOrDefault(b => Equals(b.Content, "详情") || Equals(b.Content, "编辑") || Equals(b.Content, "冲突"));
                        var moreAction = rowButtons.FirstOrDefault(b => Equals(b.Content, "⋯"));
                        Check(primaryAction != null && primaryAction.ActualWidth >= 47 && moreAction != null && moreAction.ActualWidth >= 31,
                            "glossary row actions remain visible and clickable " + size);
                    }
                    vm.AddTermCommand.Execute(null);
                    await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                    var drawer = (FrameworkElement)glossary.FindName("TermEditorDrawer");
                    Check(drawer.ActualWidth <= host.ActualWidth && drawer.ActualHeight >= 160, "drawer uses bounded page height " + size);
                    Capture(window, $"compact-term-drawer-{size.Width}-{size.Height}");
                    vm.CancelTermDrawer();
                }
                if (page == "batch")
                {
                    if (size.Width == 1024)
                    {
                        var rows = CreateMinimumTableRows();
                        try
                        {
                            foreach (var row in rows) vm.DrawingFiles.Add(row);
                            await NavIdleAsync();
                            Capture(window, "minimum-batch-with-rows");
                            var output = System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("DWGC2E_UI_SMOKE_OUTPUT") ?? "artifacts/ui-smoke");
                            CapturePhysicalWindow(window, System.IO.Path.Combine(output, "physical-minimum-batch-with-rows.png"));
                        }
                        finally { foreach (var row in rows) vm.DrawingFiles.Remove(row); }
                    }
                    var batch = FindVisual<BatchTasksPage>(window);
                    var taskTable = (DataGrid)batch.FindName("TaskTable");
                    Check(taskTable.Columns[0] is DataGridTemplateColumn
                          && FindVisuals<Button>(batch).Any(x => Equals(x.Content, "批量导出所选")),
                        "task centre keeps selection and batch export controls " + size);
                    Check(taskTable.ActualHeight >= 40, "batch retains usable table viewport " + size);
                    Check(ScrollViewer.GetHorizontalScrollBarVisibility(taskTable) == ScrollBarVisibility.Disabled,
                        "batch table does not rely on horizontal scrolling " + size);
                    var summaryMetrics = (FrameworkElement)batch.FindName("BatchSummaryMetrics");
                    var batchLabels = FindVisuals<TextBlock>(summaryMetrics).Where(x => x.IsVisible).Select(x => x.Text).ToHashSet();
                    Check(new[] { "全部", "运行中", "待处理", "未校对", "待导出", "已导出", "失败" }.All(batchLabels.Contains),
                        "batch summary exposes all seven workflow metrics " + size);
                    var actionHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(taskTable)
                        .Single(x => Equals(x.Content, "操作"));
                    var actionRight = actionHeader.TranslatePoint(new Point(actionHeader.ActualWidth, 0), taskTable).X;
                    Console.WriteLine($"TASKTABLE_WIDTH {size} table={taskTable.ActualWidth:F1} actionRight={actionRight:F1}");
                    Check(actionHeader.IsVisible && actionRight <= taskTable.ActualWidth + 1,
                        "batch action column is not clipped " + size);
                    if (size.Width == 1024)
                    {
                        // §自适应（用户 2026-09-25 选定"次要列合并进一个单元格"）：窄档把阶段/进度/最近更新
                        // 并成一格，而不是按比例压缩所有列把「查看详情」压掉。
                        Check(taskTable.Columns.Single(c => Equals(c.Header, "阶段与进度")).Visibility == Visibility.Visible
                              && taskTable.Columns.Single(c => Equals(c.Header, "阶段")).Visibility == Visibility.Collapsed
                              && taskTable.Columns.Single(c => Equals(c.Header, "进度")).Visibility == Visibility.Collapsed
                              && taskTable.Columns.Single(c => Equals(c.Header, "最近更新")).Visibility == Visibility.Collapsed,
                            "narrow window merges the task table's secondary columns into one cell");
                        var narrowTaskAction = taskTable.Columns.Single(c => Equals(c.Header, "操作")).ActualWidth;
                        Check(Math.Abs(narrowTaskAction - 118) < 0.6,
                            $"the task table action column keeps its declared width at the minimum window (actual={narrowTaskAction:F1})");
                        var taskTail = taskTable.Columns.Single(c => Equals(c.Header, ""));
                        var taskLastHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(taskTable)
                            .Single(c => Equals(c.Content, "阶段与进度"));
                        var taskRight = taskLastHeader.TranslatePoint(new Point(taskLastHeader.ActualWidth, 0), taskTable).X;
                        Check(taskTail.Visibility == Visibility.Collapsed && taskTable.ActualWidth - taskRight <= 18,
                            $"minimum task table allocates spare width to content, not an empty tail (gap={taskTable.ActualWidth - taskRight:F1})");
                    }
                    else if (size.Width >= 1120)
                    {
                        var taskTail = taskTable.Columns.Single(c => Equals(c.Header, ""));
                        Check(taskTail.Visibility == Visibility.Collapsed && taskTable.ActualWidth - actionRight <= 18,
                            $"wide task table fills the row with real columns, not an empty tail (gap={taskTable.ActualWidth - actionRight:F1}, window={size.Width})");
                        Check(taskTable.Columns.Single(c => Equals(c.Header, "任务 / 图纸")).ActualWidth >= 184
                              && taskTable.Columns.Single(c => Equals(c.Header, "最近更新")).ActualWidth >= 152,
                            "wide task table preserves readable filename and timestamp columns " + size);
                    }

                    var previousTask = vm.SelectedBatchTask;
                    if (size.Width is 1366 or 1440)
                    {
                        var now = DateTime.Now;
                        var outputPath = System.IO.Path.Combine(AppDataDir, $"batch-exported-{size.Width}.dwg");
                        System.IO.File.WriteAllText(outputPath, "controlled exported fixture");
                        var failed = new DrawingFileItem(System.IO.Path.Combine(AppDataDir, $"batch-failed-{size.Width}.dwg"));
                        failed.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(failed.FullPath)
                        {
                            Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.Failed,
                            Error = "受控失败原因：Provider 超时",
                            Progress = 42,
                            TextCount = 20,
                            TranslatedCount = 8,
                            FailedCount = 12,
                            UpdatedAt = now.AddDays(-10),
                            RetryCount = 2
                        });
                        var review = new DrawingFileItem(System.IO.Path.Combine(AppDataDir, $"batch-review-{size.Width}.dwg"));
                        review.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(review.FullPath)
                        {
                            Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.ReadyForReview,
                            Progress = 100,
                            TextCount = 18,
                            TranslatedCount = 18,
                            StartedAt = now.AddMinutes(-8),
                            CompletedAt = now.AddMinutes(-2),
                            UpdatedAt = now.AddMinutes(-2)
                        });
                        var completed = new DrawingFileItem(System.IO.Path.Combine(AppDataDir, $"batch-completed-{size.Width}.dwg"));
                        completed.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(completed.FullPath)
                        {
                            Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed,
                            Progress = 100,
                            TextCount = 21,
                            TranslatedCount = 21,
                            StartedAt = now.AddMinutes(-12),
                            CompletedAt = now.AddMinutes(-5),
                            ReviewCompletedAt = now.AddMinutes(-1),
                            UpdatedAt = now.AddMinutes(-1)
                        });
                        var exported = new DrawingFileItem(System.IO.Path.Combine(AppDataDir, $"batch-exported-source-{size.Width}.dwg"));
                        exported.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(exported.FullPath)
                        {
                            Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed,
                            Progress = 100,
                            TextCount = 15,
                            TranslatedCount = 15,
                            StartedAt = now.AddMinutes(-20),
                            CompletedAt = now.AddMinutes(-10),
                            ReviewCompletedAt = now.AddMinutes(-8),
                            LastExportedAt = now.AddMinutes(-3),
                            UpdatedAt = now.AddMinutes(-3),
                            LastExportPath = outputPath
                        });
                        var fixtures = new[] { failed, review, completed, exported };
                        var oldStatusFilter = vm.BatchStatusFilter;
                        var oldDateFilter = vm.BatchDateFilter;
                        var oldSearch = vm.BatchSearch;
                        try
                        {
                            foreach (var row in fixtures) vm.DrawingFiles.Add(row);
                            await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                            taskTable.ScrollIntoView(failed);
                            taskTable.UpdateLayout();
                            if (size.Width == 1440)
                            {
                                Capture(window, "wide-batch-with-rows-1440");
                                var output = System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("DWGC2E_UI_SMOKE_OUTPUT") ?? "artifacts/ui-smoke");
                                CapturePhysicalWindow(window, System.IO.Path.Combine(output, "physical-wide-batch-with-rows-1440.png"));
                            }

                            Check(review.WorkflowStatusText == "待导出" && completed.WorkflowStatusText == "待导出" && exported.WorkflowStatusText == "已导出",
                                "batch rows distinguish export-ready and exported states " + size);
                            Check(!failed.CanOpenProofreading && failed.CanRetry && !failed.CanExport,
                                "failed task only exposes retry semantics " + size);
                            // 「清空已结束任务」的**回收范围**（用户批注 2026-09-23：「为什么不能删除任务」）。
                            // 这里只断言"谁会/不会被打上清理标记"——不执行删除，因为删行会让本段后续断言
                            // （抽屉、筛选）失去夹具；真正的移除机制复用已被覆盖的 RemoveDrawing 路径。
                            Check(exported.IsCleanupFinished && failed.IsCleanupFinished
                                    && !review.IsCleanupFinished && !completed.IsCleanupFinished,
                                "clear-finished targets only exported and failed rows " + size);
                            Check(vm.CanClearFinishedBatchTasks,
                                "clear-finished is offered while finished rows exist " + size);
                            // 校对可选（2026-09-22）：未校对的行既能校对也能直接导出，但同样不能重试。
                            Check(review.CanOpenProofreading && !review.CanRetry && review.CanExport,
                                "unproofread task can be proofread and exported directly " + size);
                            Check(completed.CanOpenProofreading && completed.CanExport && !completed.CanRetry,
                                "proofread task exposes the same export semantics " + size);
                            Check(exported.CanOpenProofreading && exported.HasOutput && !exported.CanExport,
                                "exported task exposes the existing output " + size);

                            vm.SelectedBatchTask = exported;
                            vm.IsTaskDetailOpen = true;
                            await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                            var exportedDrawer = (FrameworkElement)batch.FindName("TaskDetailDrawer");
                            var exportedDrawerButtons = FindVisuals<Button>(exportedDrawer).Where(x => x.IsVisible).ToList();
                            Check(exportedDrawerButtons.Count(x => Equals(x.Content, "打开输出文件")) == 1,
                                "exported task drawer exposes exactly one open-output action " + size);
                            Check(exportedDrawerButtons.Count(x => Equals(x.Content, "重新导出")) == 1,
                                "exported task drawer exposes one explicit re-export action " + size);

                            // The focus test below must exercise an actual hidden -> visible transition.
                            // Keep the exported-task assertions independent, then close this drawer so
                            // opening the failed task queues a fresh FocusOnOpen callback.
                            vm.IsTaskDetailOpen = false;
                            await WaitUntil(
                                () => !vm.IsTaskDetailOpen && !exportedDrawer.IsVisible,
                                "exported task drawer did not close before the focus regression fixture");

                            vm.BatchSearch = "batch-";
                            vm.BatchStatusFilter = 3;
                            Check(vm.BatchView.Cast<object>().Contains(review) && !vm.BatchView.Cast<object>().Contains(completed),
                                "batch unreviewed filter selects only unproofread fixtures " + size);
                            // 过滤器 4 现在等于"可导出"，未校对与已校对两张都在里面（2026-09-22 校对解耦）。
                            vm.BatchStatusFilter = 4;
                            Check(vm.BatchView.Cast<object>().Contains(completed) && vm.BatchView.Cast<object>().Contains(review)
                                && !vm.BatchView.Cast<object>().Contains(exported),
                                "batch export filter covers both proofread and unproofread export-ready rows " + size);
                            vm.BatchStatusFilter = 5;
                            Check(vm.BatchView.Cast<object>().Contains(exported), "batch exported filter uses an existing output " + size);
                            vm.BatchStatusFilter = 6;
                            Check(vm.BatchView.Cast<object>().Contains(failed), "batch failure filter uses the documented index " + size);
                            vm.BatchStatusFilter = 0;
                            vm.BatchDateFilter = 2;
                            Check(!vm.BatchView.Cast<object>().Contains(failed) && vm.BatchView.Cast<object>().Contains(review),
                                "batch recent-date filter uses UpdatedAt rather than CreatedAt " + size);
                            vm.BatchDateFilter = 0;

                            taskTable.ScrollIntoView(failed);
                            taskTable.UpdateLayout();
                            var failedRow = (DataGridRow?)taskTable.ItemContainerGenerator.ContainerFromItem(failed)
                                ?? throw new InvalidOperationException("Failed-task row was not realized for the focus regression fixture.");
                            var detailButton = FindVisuals<Button>(failedRow).Single(x => Equals(x.Content, "查看详情"));
                            Check(detailButton.Focus() && ReferenceEquals(Keyboard.FocusedElement, detailButton),
                                "task detail trigger receives keyboard focus before opening the drawer");
                            detailButton.Command.Execute(detailButton.CommandParameter);
                            await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                            var taskDrawer = (FrameworkElement)batch.FindName("TaskDetailDrawer");
                            var drawerButtons = FindVisuals<Button>(taskDrawer).ToList();
                            Check(taskDrawer.ActualHeight >= 160 && taskDrawer.ActualWidth <= host.ActualWidth,
                                "task drawer retains page height " + size);
                            Check(!drawerButtons.Single(x => Equals(x.Content, "进入译文校对")).IsEnabled
                                && drawerButtons.Single(x => Equals(x.Content, "重试此任务")).IsVisible
                                && !drawerButtons.Single(x => Equals(x.Content, "导出此图纸")).IsVisible,
                                "failed task drawer cannot proofread or export " + size);
                            Check(FindVisuals<TextBlock>(taskDrawer).Any(x => x.IsVisible && x.Text == "阶段时间线")
                                && FindVisuals<TextBlock>(taskDrawer).Any(x => x.IsVisible && x.Text.Contains("Provider 超时")),
                                "task drawer exposes timeline and full failure reason " + size);
                            var drawerScroll = FindVisuals<ScrollViewer>(taskDrawer).Single(x => x.IsVisible);
                            Check(ResponsiveLayout.GetHandoffMouseWheelAtBoundary(drawerScroll),
                                "task drawer hands wheel input to the page at scroll boundaries " + size);
                            Check(ResponsiveLayout.GetHandoffMouseWheelAtBoundary(taskTable),
                                "DataGrid precision wheel behavior is applied globally " + size);
                            if (size.Width == 1366)
                            {
                                var closeButton = (Button)batch.FindName("TaskDrawerCloseButton");
                                await WaitUntil(
                                    () => ReferenceEquals(Keyboard.FocusedElement, closeButton),
                                    "task drawer did not move keyboard focus to its close action");
                                Check(ReferenceEquals(Keyboard.FocusedElement, closeButton),
                                    "task drawer moves keyboard focus to its close action");
                                var closePeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(closeButton);
                                ((System.Windows.Automation.Provider.IInvokeProvider)closePeer.GetPattern(
                                    System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                                await WaitUntil(
                                    () => !vm.IsTaskDetailOpen && !taskDrawer.IsVisible,
                                    "task drawer did not close after invoking its close action");
                                await WaitUntil(
                                    () => !ReferenceEquals(Keyboard.FocusedElement, closeButton)
                                          && (ReferenceEquals(Keyboard.FocusedElement, detailButton) || taskTable.IsKeyboardFocusWithin),
                                    "task drawer focus restoration did not complete");
                                Check(!ReferenceEquals(Keyboard.FocusedElement, closeButton)
                                      && (ReferenceEquals(Keyboard.FocusedElement, detailButton) || taskTable.IsKeyboardFocusWithin),
                                    $"task drawer restores focus to its trigger or stable task-table fallback; focused={Keyboard.FocusedElement?.GetType().FullName ?? "<null>"}");
                                vm.IsTaskDetailOpen = true;
                                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                            }
                            Capture(window, $"batch-workflow-drawer-{size.Width}-{size.Height}");

                            vm.SelectedBatchTask = completed;
                            await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                            drawerButtons = FindVisuals<Button>(taskDrawer).ToList();
                            Check(drawerButtons.Single(x => Equals(x.Content, "进入译文校对")).IsEnabled
                                && drawerButtons.Single(x => Equals(x.Content, "导出此图纸")).IsVisible,
                                "completed task drawer enables proofreading and export " + size);
                            Capture(window, $"batch-workflow-{size.Width}-{size.Height}");
                        }
                        finally
                        {
                            vm.IsTaskDetailOpen = false;
                            vm.BatchSearch = oldSearch;
                            vm.BatchStatusFilter = oldStatusFilter;
                            vm.BatchDateFilter = oldDateFilter;
                            foreach (var row in fixtures) vm.DrawingFiles.Remove(row);
                            try { System.IO.File.Delete(outputPath); } catch { }
                            vm.SelectedBatchTask = previousTask;
                            await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                        }
                    }
                    else
                    {
                        vm.SelectedBatchTask = vm.DrawingFiles.FirstOrDefault() ?? new DrawingFileItem(System.IO.Path.Combine(AppDataDir, "isolated-error-fixture.dwg"), "隔离测试：文件读取失败，请检查文件并重试。");
                        vm.IsTaskDetailOpen = true;
                        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                        var taskDrawer = (FrameworkElement)batch.FindName("TaskDetailDrawer");
                        Check(taskDrawer.ActualHeight >= 160 && taskDrawer.ActualWidth <= host.ActualWidth, "task drawer retains page height " + size);
                        // Wait for the intentional slide-in to settle before treating the drawer's
                        // transient offscreen frame as a layout failure or capturing a clipped image.
                        await Task.Delay(450);
                        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                        var drawerRight = taskDrawer.TranslatePoint(new Point(taskDrawer.ActualWidth, 0), batch).X;
                        Check(drawerRight <= batch.ActualWidth + 0.5 && drawerRight <= host.ActualWidth + 0.5,
                            $"task drawer settles inside the visible page {size}: right={drawerRight:F1}, page={batch.ActualWidth:F1}");
                        Capture(window, $"compact-task-drawer-{size.Width}-{size.Height}");
                        vm.IsTaskDetailOpen = false;
                        vm.SelectedBatchTask = previousTask;
                    }
                }
                if (page == "account" && size.Width == 1024)
                {
                    // §自适应：会员卡在窄档上下堆叠（阈值 1100 现在来自共享令牌 Size.BreakpointCards）。
                    var accountPage = FindVisual<AccountPage>(window);
                    var devicesCard = (Border)accountPage.FindName("DevicesCard");
                    Check(Grid.GetRow(devicesCard) == 1 && Grid.GetColumnSpan(devicesCard) == 2,
                        $"narrow window stacks the membership cards (row={Grid.GetRow(devicesCard)}, span={Grid.GetColumnSpan(devicesCard)})");
                }
                if (page == "settings")
                {
                    var settings = FindVisual<SettingsPage>(window);
                    Check(((FrameworkElement)settings.FindName("CompactSections")).IsVisible == expectedCompact, "settings switches navigation " + size);
                    if (expectedCompact)
                        Check(settings.FindName("CompactSections") is ListBox tabs && tabs.Items.Count == 6
                              && tabs.Items.Cast<ListBoxItem>().All(x => x.IsVisible),
                            "compact settings exposes six section buttons " + size);
                    if (expectedCompact)
                    {
                        var numberedTabs = (ListBox)settings.FindName("CompactSections");
                        var numbers = FindVisuals<TextBlock>(numberedTabs).Where(x => x.IsVisible)
                            .Select(x => x.Text).ToHashSet();
                        Check(Enumerable.Range(1, 6).All(n => numbers.Contains(n.ToString("00"))),
                            "compact settings keeps the six numbered section chips " + size);
                    }
                    for (var section = 0; section < 6; section++)
                    {
                        vm.SettingsSection = section;
                        await NavIdleAsync();
                        if (section is 4 or 5)
                            Check(FindVisuals<TextBlock>(settings).Any(x => x.IsVisible && x.Text.StartsWith($"SECTION 0{section + 1}")),
                                $"settings section {section + 1} keeps its numbered kicker " + size);
                        Capture(window, $"compact-settings-{section}-{size.Width}-{size.Height}");
                    }
                    vm.SettingsSection = 0;
                }
                Capture(window, $"compact-{page}-{size.Width}-{size.Height}");
            }
        }
        await VerifyContinuousResizeAsync(window, vm);
        await VerifyPrecisionWheelHandoffAsync();
        Check(window.MinWidth <= 1024 && window.MinHeight <= 640 && window.MinWidth >= 800 && window.MinHeight >= 560,
            $"desktop shell minimum stays usable and matches the width every page can reflow into (min={window.MinWidth}x{window.MinHeight})");
        FitNativeWindow(window, new Size(1366, 768));
        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        vm.CurrentPage = previous;
        await NavIdleAsync();
        // Standalone host avoids consuming actual view-model notifications in the shell.
        var toast = new ToastHost();
        var paused = false; toast.ShouldPause = () => paused;
        var toastWindow = new Window { Width = 400, Height = Math.Min(900, SystemParameters.WorkArea.Height - 20), Content = toast, ShowInTaskbar = false };
        toastWindow.Show();
        try
        {
            await WaitForStableAsync(() => toastWindow.ActualHeight, "isolated toast window height");
            var expectedToastCapacity = Math.Clamp(Math.Min(toast.MaximumVisible,
                toastWindow.ActualHeight < 600 ? 1 : toastWindow.ActualHeight < 900 ? 2 : 3), 1, 3);
            for (var i = 0; i < 5; i++) toast.Show("测试反馈 " + i, Brushes.Brown);
            toast.Show("测试反馈 0", Brushes.Brown);
            Check(toast.VisibleCount == expectedToastCapacity && toast.PendingCount == 5 - expectedToastCapacity,
                "toast respects viewport capacity and merges duplicate messages");
            paused = true; await Task.Delay(300);
            Check(toast.Visibility == Visibility.Collapsed, "paused toast region occupies no layout space");
            await Task.Delay(4200);
            Check(toast.VisibleCount == expectedToastCapacity && toast.PendingCount == 5 - expectedToastCapacity,
                "pause retains notifications and lifetime");
            paused = false; await Task.Delay(300);
            Check(toast.Visibility == Visibility.Visible, "notifications resume after pause");
            Capture(toastWindow, "toast-queued-isolated");
        }
        finally { toastWindow.Close(); }
    }
    private async Task VerifyPrecisionWheelHandoffAsync()
    {
        var grid = new DataGrid
        {
            Height = 96,
            AutoGenerateColumns = true,
            ItemsSource = Enumerable.Range(0, 80).Select(index => new { Value = $"Row {index}" }).ToList()
        };
        ScrollViewer.SetCanContentScroll(grid, true);
        ResponsiveLayout.SetHandoffMouseWheelAtBoundary(grid, true);

        var content = new StackPanel();
        content.Children.Add(grid);
        content.Children.Add(new Border { Height = 480 });
        var outer = new ScrollViewer
        {
            Height = 160,
            Width = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
        var testWindow = new Window
        {
            Width = 400,
            Height = 220,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = outer
        };
        testWindow.Show();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var inner = FindVisual<ScrollViewer>(grid);
            Check(inner.ScrollableHeight > 0 && outer.ScrollableHeight > 0,
                "precision wheel fixture has nested scrollable surfaces");

            static void RaiseWheel(UIElement target, int delta)
            {
                target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent
                });
            }

            inner.ScrollToTop();
            outer.ScrollToTop();
            RaiseWheel(grid, -15);
            RaiseWheel(grid, -15);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(inner.VerticalOffset <= 0.5,
                "precision touchpad deltas accumulate without being discarded");
            RaiseWheel(grid, -15);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(inner.VerticalOffset > 0.5,
                "precision touchpad deltas produce deterministic inner scrolling");

            inner.ScrollToEnd();
            outer.ScrollToTop();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            RaiseWheel(grid, -120);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(outer.VerticalOffset > 0.5,
                "DataGrid wheel input hands off to the parent at the inner boundary");
        }
        finally
        {
            testWindow.Close();
        }
    }

    private DrawingFileItem[] CreateMinimumTableRows()
    {
        var now = DateTime.Now;
        return Enumerable.Range(1, 3).Select(index =>
        {
            var row = new DrawingFileItem(System.IO.Path.Combine(AppDataDir, $"minimum-layout-{index}.dxf"));
            row.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(row.FullPath)
            {
                Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed,
                Progress = 100,
                TextCount = 120 * index,
                TranslatedCount = 120 * index,
                UpdatedAt = now.AddMinutes(-index)
            });
            return row;
        }).ToArray();
    }

}










