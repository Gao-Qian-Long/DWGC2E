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
        // §L5 1120×640 是最小支持尺寸，该尺寸下 compact 分支必须真正生效，随后恢复为非 compact。
        window.Width = 1120; window.Height = 640;
        await WaitForStableAsync(() => window.ActualWidth, "window width 1120x640");
        await WaitForStableAsync(() => host.ActualWidth, "page canvas 1120x640");
        var sidebarProbe = (System.Windows.Controls.ColumnDefinition)window.FindName("SidebarColumn");
        Console.WriteLine($"INFO compact probe: actual={window.ActualWidth:F1}x{window.ActualHeight:F1} content={((FrameworkElement)window.Content).ActualWidth:F1} sidebar={sidebarProbe.Width.Value:F1} host={host.ActualWidth:F1} pageMargin={window.Resources["Spacing.Page"]}");
        Check(ResponsiveLayout.GetIsCompact(window) && (window.MinWidth <= 1120 && window.MinHeight <= 640),
            $"compact layout engages at the supported minimum window size (min={window.MinWidth}x{window.MinHeight}, actual={window.ActualWidth:F1}x{window.ActualHeight:F1}, content={((FrameworkElement)window.Content).ActualWidth:F1}, host={host.ActualWidth:F1}, sidebar={sidebarProbe.Width.Value:F1}, compact={ResponsiveLayout.GetIsCompact(window)})");
        Check(Math.Abs(((System.Windows.Controls.ColumnDefinition)window.FindName("SidebarColumn")).Width.Value - 64) < 0.1,
            "compact sidebar collapses to the 64 DIP icon rail");
        vm.CurrentPage = MainViewModel.PageSettings;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var compactSettings = FindVisual<SettingsPage>(window);
        Check(((FrameworkElement)compactSettings.FindName("CompactSections")).IsVisible,
            "compact window exposes the section picker instead of the hidden nav column");
        vm.CurrentPage = "translate";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.Width = 1280; window.Height = 720;
        await WaitForStableAsync(() => window.ActualWidth, "window width 1280x720");
        Check(!ResponsiveLayout.GetIsCompact(window), "leaving the minimum restores the full sidebar");
        foreach (var size in new[] { new Size(1280,720), new Size(1366,768), new Size(1440,900), new Size(1920,1080) })
        {
            window.Width = size.Width; window.Height = size.Height;
            // 窗口尺寸由 OS/合成器异步生效：必须等客户区真正稳定后再量，否则会读到旧尺寸。
            await WaitForStableAsync(() => window.ActualWidth, $"window width {size.Width}x{size.Height}");
            await WaitForStableAsync(() => host.ActualWidth, $"page canvas {size.Width}x{size.Height}");
            var expectedCompact = false; // The desktop shell enforces a usable normal-workspace minimum.
            Check(ResponsiveLayout.GetIsCompact(window) == expectedCompact, "sidebar breakpoint " + size);
            Check(host.ActualWidth <= size.Width - (expectedCompact ? 64 : 224) + 1, $"no oversized minimum page canvas {size} (host={host.ActualWidth:F1}, window={window.ActualWidth:F1}, page={vm.CurrentPage})");
            foreach (var page in new[] { "translate", "batch", "glossary", "account", "settings" })
            {
                vm.CurrentPage = page;
                await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
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
                    var metricLabels = FindVisuals<TextBlock>(metrics).Where(x => x.IsVisible).Select(x => x.Text).ToHashSet();
                    Check(new[] { "图纸", "运行状态", "整体进度", "翻译成功", "失败", "待校对", "待导出" }.All(metricLabels.Contains),
                        "translation summary exposes all seven workflow metrics " + size);
                    Check(nextStep.IsVisible && nextStep.ActualWidth >= 80,
                        "translation next-step guidance remains visible " + size);

                    if (size.Width == 1280 && size.Height == 720)
                    {
                        var chooseFile = (Button)translate.FindName("EmptySelectFileButton");
                        var bottom = chooseFile.TranslatePoint(new Point(0, chooseFile.ActualHeight), root).Y;
                        Check(chooseFile.IsVisible && chooseFile.ActualHeight >= 30 && bottom <= root.ActualHeight + 1,
                            "minimum window keeps primary file CTA fully visible");
                    }

                    if (size.Width is 1280 or 1366)
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
                            var actionHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(queue)
                                .Single(x => Equals(x.Content, "操作"));
                            var actionRight = actionHeader.TranslatePoint(new Point(actionHeader.ActualWidth, 0), queue).X;
                            Check(actionHeader.IsVisible && actionRight <= queue.ActualWidth + 1,
                                "translation action column is not clipped " + size);
                            Check(review.WorkflowStatusText == "待校对" && completed.WorkflowStatusText == "待导出",
                                "translation rows distinguish review and export workflow states " + size);
                            Check(vm.WorkspaceReviewCount == 1 && vm.WorkspacePendingExportCount == 1 && vm.WorkspaceFailedCount == 1,
                                "translation summary follows drawing collection changes " + size);

                            var export = (Button)translate.FindName("ExportSelectionButton");
                            var retry = (Button)translate.FindName("RetryFailedButton");
                            completed.IsIncludedForExport = false;
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            Check(retry.IsEnabled && vm.CanRetryFailedDrawingTasks,
                                "failed drawings enable the dedicated retry action " + size);
                            Check(!vm.CanStartWorkspaceTranslation,
                                "failed drawings do not incorrectly enable the start-translation action " + size);
                            Check(!export.IsEnabled && !vm.CanExportWorkspace,
                                "ready-for-review drawing cannot skip proofreading and export " + size);
                            completed.IsIncludedForExport = true;
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            Check(export.IsEnabled && vm.CanExportWorkspace,
                                "reviewed drawing waiting for export enables export " + size);

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
                    var batch = FindVisual<BatchTasksPage>(window);
                    var taskTable = (DataGrid)batch.FindName("TaskTable");
                    Check(taskTable.ActualHeight >= 40, "batch retains usable table viewport " + size);
                    Check(ScrollViewer.GetHorizontalScrollBarVisibility(taskTable) == ScrollBarVisibility.Disabled,
                        "batch table does not rely on horizontal scrolling " + size);
                    var summaryMetrics = (FrameworkElement)batch.FindName("BatchSummaryMetrics");
                    var batchLabels = FindVisuals<TextBlock>(summaryMetrics).Where(x => x.IsVisible).Select(x => x.Text).ToHashSet();
                    Check(new[] { "全部", "运行中", "待处理", "待校对", "待导出", "已导出", "失败" }.All(batchLabels.Contains),
                        "batch summary exposes all seven workflow metrics " + size);
                    var actionHeader = FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(taskTable)
                        .Single(x => Equals(x.Content, "操作"));
                    var actionRight = actionHeader.TranslatePoint(new Point(actionHeader.ActualWidth, 0), taskTable).X;
                    Check(actionHeader.IsVisible && actionRight <= taskTable.ActualWidth + 1,
                        "batch action column is not clipped " + size);

                    var previousTask = vm.SelectedBatchTask;
                    if (size.Width is 1280 or 1366)
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

                            Check(review.WorkflowStatusText == "待校对" && completed.WorkflowStatusText == "待导出" && exported.WorkflowStatusText == "已导出",
                                "batch rows distinguish review, export and exported states " + size);
                            Check(!failed.CanOpenProofreading && failed.CanRetry && !failed.CanExport,
                                "failed task only exposes retry semantics " + size);
                            Check(review.CanOpenProofreading && !review.CanRetry && !review.CanExport,
                                "review task can proofread but cannot skip to export " + size);
                            Check(completed.CanOpenProofreading && completed.CanExport && !completed.CanRetry,
                                "reviewed task exposes export semantics " + size);
                            Check(exported.CanOpenProofreading && exported.HasOutput && !exported.CanExport,
                                "exported task exposes the existing output " + size);

                            vm.BatchSearch = "batch-";
                            vm.BatchStatusFilter = 3;
                            Check(vm.BatchView.Cast<object>().Contains(review) && !vm.BatchView.Cast<object>().Contains(completed),
                                "batch review filter selects only review-stage fixtures " + size);
                            vm.BatchStatusFilter = 4;
                            Check(vm.BatchView.Cast<object>().Contains(completed) && !vm.BatchView.Cast<object>().Contains(review),
                                "batch export filter selects only reviewed fixtures " + size);
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
                            if (size.Width == 1280)
                            {
                                var closeButton = (Button)batch.FindName("TaskDrawerCloseButton");
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
                        Capture(window, $"compact-task-drawer-{size.Width}-{size.Height}");
                        vm.IsTaskDetailOpen = false;
                        vm.SelectedBatchTask = previousTask;
                    }
                }                if (page == "settings")
                {
                    var settings = FindVisual<SettingsPage>(window);
                    Check(((FrameworkElement)settings.FindName("CompactSections")).IsVisible == expectedCompact, "settings switches navigation " + size);
                    for (var section = 0; section < 6; section++)
                    {
                        vm.SettingsSection = section;
                        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                        Capture(window, $"compact-settings-{section}-{size.Width}-{size.Height}");
                    }
                    vm.SettingsSection = 0;
                }
                Capture(window, $"compact-{page}-{size.Width}-{size.Height}");
            }
        }
        await VerifyPrecisionWheelHandoffAsync();
        Check(window.MinWidth <= 1120 && window.MinHeight <= 640 && window.MinWidth >= 800 && window.MinHeight >= 560,
            $"desktop shell minimum stays usable and never exceeds the compact breakpoint (min={window.MinWidth}x{window.MinHeight})");
        window.Width = 1366; window.Height = 768;
        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        vm.CurrentPage = previous;
        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        // Standalone host avoids consuming actual view-model notifications in the shell.
        var toast = new ToastHost();
        var paused = false; toast.ShouldPause = () => paused;
        var toastWindow = new Window { Width = 400, Height = 900, Content = toast, ShowInTaskbar = false };
        toastWindow.Show();
        try
        {
            for (var i = 0; i < 5; i++) toast.Show("测试反馈 " + i, Brushes.Brown);
            toast.Show("测试反馈 0", Brushes.Brown);
            Check(toast.VisibleCount == 3 && toast.PendingCount == 2, "toast maximum three and duplicate merge");
            paused = true; await Task.Delay(300);
            Check(toast.Visibility == Visibility.Collapsed, "paused toast region occupies no layout space");
            await Task.Delay(4200);
            Check(toast.VisibleCount == 3 && toast.PendingCount == 2, "pause retains notifications and lifetime");
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

}










