using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DwgTranslator.App;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.App.Views.Controls;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifySmallSurfacesAsync(MainWindow owner, MainViewModel vm)
    {
        async Task Surface(Window dialog, string name)
        {
            dialog.Owner = owner; dialog.ShowInTaskbar = false;
            dialog.Show();
            try
            {
                await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                if (name == "log-viewer-empty")
                {
                    var panel = FindVisual<LogViewerPanel>(dialog);
                    Check(panel != null, "log viewer panel is visible");
                    Check(((FrameworkElement)panel.FindName("LogEmptyState")).IsVisible, "empty log viewer explains that it is waiting for runtime logs");
                    Check(((TextBlock)panel.FindName("LogEmptyStateTitle")).Text == "暂无运行日志", "empty log viewer has explicit idle title");
                }
                Capture(dialog, "dialog-" + name);
            }
            finally { dialog.Close(); }
        }
        await VerifyImportAvailabilityAsync(owner, vm);
        await Surface(new LanguagePairDialog("ZH", "EN"), "language-pair");
        await Surface(new ExportModeDialog(true), "export-cad-available");
        await Surface(new ExportModeDialog(false, true), "translation-offline-option");
        await Surface(new LicenseDialog(App.Services!.GetRequiredService<ILicenseService>()), "license-status");
        await Surface(new SavedOutputsWindow(new[] { System.IO.Path.Combine(AppDataDir, "isolated-output-fixture.dwg") }), "saved-outputs");
        await Surface(new Window { Width = 820, Height = 600, Content = new HelpPanel(), Title = "帮助（隔离测试）" }, "help");
        await Surface(new Window { Width = 820, Height = 600, Content = new LogViewerPanel { DataContext = new LogViewModel() }, Title = "日志（隔离测试）" }, "log-viewer-empty");
        var id = Guid.NewGuid().ToString("D");
        var basis = new CloudGlossaryEntry { Id = id, Source = "轴承", Target = "Bearing", SourceLang = "ZH", TargetLang = "EN" };
        var local = new CloudGlossaryEntry { Id = id, Source = "轴承", Target = "Local bearing", SourceLang = "ZH", TargetLang = "EN" };
        var remote = new CloudGlossaryEntry { Id = id, Source = "轴承", Target = "Remote bearing", SourceLang = "ZH", TargetLang = "EN" };
        var merge = new GlossaryMergeDialog(CloudGlossaryMerge.Plan(new[] { basis }, new[] { local }, new[] { remote }), () => true);
        await Surface(new Window { Width = 600, Height = 480, Content = merge, Title = "合并冲突（隔离测试）" }, "glossary-merge-conflict");
        merge.Close();
        void Modal(Action show, string name)
        {
            Exception? failure = null; bool captured = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) =>
            {
                var modal = Windows.OfType<Window>().LastOrDefault(w => w != owner && w.IsVisible);
                if (modal == null) return;
                timer.Stop();
                try
                {
                    Capture(modal, "dialog-" + name); captured = true;
                    if (name == "category-management")
                        Check(FindVisuals<Button>(modal).Single(b => Equals(b.Content, "删除并迁回默认分类")).IsEnabled == false, "default category cannot be deleted from dialog");
                    if (modal.Content is DependencyObject content && FindVisuals<ToastHost>(content).FirstOrDefault() is { } feedback)
                    {
                        DwgTranslator.App.Services.ToastService.Info("隔离测试：当前弹窗操作反馈");
                        Check(feedback.VisibleCount == 1, "modal operation feedback routes to current dialog");
                        modal.UpdateLayout(); Capture(modal, "dialog-" + name + "-feedback");
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { modal.Close(); }
            };
            timer.Start(); try { show(); } finally { timer.Stop(); }
            if (failure != null) throw failure;
            Check(captured, "modal screenshot captured " + name);
        }
        Modal(() => PromptDialog.Show("这是一条隔离测试确认消息。取消不会执行任何业务操作。", "确认操作", MessageBoxButton.YesNoCancel), "confirmation-three-actions");
        Modal(() => GlossaryManagementWindow.ShowCategories(vm), "category-management");
        Modal(() => GlossaryManagementWindow.ChooseDirection(), "term-direction");
        Modal(() => GlossaryCloudWindow.Choose(new[] { basis }, "isolated-test-account", DateTime.UtcNow), "cloud-terms");
        var account = (Button)owner.FindName("AccountMenuButton");
        account.ContextMenu.PlacementTarget = account; account.ContextMenu.DataContext = vm; account.ContextMenu.IsOpen = true;
        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        Capture(account.ContextMenu, "popup-account-menu"); account.ContextMenu.IsOpen = false;
        var banner = (AnnouncementBanner)owner.FindName("SiteAnnouncement");

        banner.IsOpen = true;
        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        Capture(Application.Current.Windows.OfType<AnnouncementWindow>().Single(), "popup-announcement-details"); banner.IsOpen = false;
    }

    private async Task VerifyImportAvailabilityAsync(MainWindow owner, MainViewModel vm)
    {
        var previousPage = vm.CurrentPage;
        try
        {
            vm.CurrentPage = MainViewModel.PageBatch;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var page = FindVisual<DwgTranslator.App.Views.Pages.BatchTasksPage>(owner);
            var addDrawing = FindVisuals<Button>(page).Single(button => Equals(button.Content, "添加图纸"));
            Check(addDrawing.IsVisible && addDrawing.IsEnabled && vm.CanImportFiles,
                "empty batch-task state exposes an enabled drawing import action");
            Check(vm.ImportDwgCommand.CanExecute(null) && vm.ImportExcelCommand.CanExecute(null),
                "CAD and spreadsheet import commands start available");

            vm.IsProcessing = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!addDrawing.IsEnabled && !vm.ImportDwgCommand.CanExecute(null) && !vm.ImportExcelCommand.CanExecute(null),
                "batch-task import action and commands disable during an operation");

            vm.IsProcessing = false;
            vm.IsLoggingIn = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!addDrawing.IsEnabled && !vm.ImportDwgCommand.CanExecute(null) && !vm.ImportExcelCommand.CanExecute(null),
                "batch-task import action and commands disable during account switching");

            vm.IsLoggingIn = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(addDrawing.IsEnabled && vm.ImportDwgCommand.CanExecute(null) && vm.ImportExcelCommand.CanExecute(null),
                "batch-task import availability restores after busy states clear");
        }
        finally
        {
            vm.IsProcessing = false;
            vm.IsLoggingIn = false;
            vm.CurrentPage = previousPage;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }
}

