using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.App.Views.Pages;
using DwgTranslator.Core.Tasks;
namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyTaskRecoveryNoticeAsync(MainWindow window, MainViewModel vm)
    {
        var manager = (DwgTranslator.Core.Tasks.TaskManager)typeof(MainViewModel).GetField("_taskManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var original = (ITaskStore)typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        var path = Path.Combine(AppDataDir, "recovery-notice-probe", "tasks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{truncated");
        var store = new JsonTaskStore(path);
        var refresh = typeof(MainViewModel).GetMethod("RefreshTaskRecoveryNotice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousPage = vm.CurrentPage;
        try
        {
            manager.SwitchAccountStore(store);
            refresh.Invoke(vm, null);
            vm.CurrentPage = MainViewModel.PageBatch;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var page = FindVisual<BatchTasksPage>(window);
            var banner = (Border)page.FindName("RecoveryNotice");
            Check(vm.HasTaskRecoveryWarning && manager.Tasks.Count == 0, "corrupt empty recovery does not silently look healthy");
            Check(banner.IsVisible && ((TextBlock)banner.Child).Text.Contains(Path.GetDirectoryName(path)!), "recovery warning displays real directory");
            Check(banner.ActualHeight > 0 && banner.ActualWidth <= page.ActualWidth, "recovery warning fits page");
            Capture(window, "task-recovery-warning");
            manager.EnsureAccountStoreSaved();
            Check(File.ReadAllText(store.RecoveryFilePath!) == "{truncated", "UI warning retains damaged original on next save");
            Check(vm.HasTaskRecoveryWarning, "safe save does not prematurely hide recovery warning");
            manager.SwitchAccountStore(original);
            refresh.Invoke(vm, null);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!vm.HasTaskRecoveryWarning && !banner.IsVisible, "switch to healthy workspace clears previous account warning");
        }
        finally
        {
            if (!ReferenceEquals(typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager), original)) manager.SwitchAccountStore(original);
            refresh.Invoke(vm, null);
            vm.CurrentPage = previousPage;
        }
    }
}
