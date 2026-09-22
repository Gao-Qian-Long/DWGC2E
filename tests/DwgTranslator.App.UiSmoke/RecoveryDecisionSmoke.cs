using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.Core.Tasks;

namespace UiSmoke;
public sealed partial class SmokeApp
{
    /// <summary>
    /// 2026-09-22 行为变更：启动不再弹「未完成的任务」模态框，改为导航红点 + 任务中心提示条。
    /// 这里验证的是新契约——启动过程零弹窗、队列与落盘文件都不被启动流程改写、
    /// 只有用户在任务中心显式点击「清除未完成记录」才删除未完成记录（终态历史始终保留）。
    /// </summary>
    private void VerifyRecoveryDecision(MainViewModel vm)
    {
        var manager = (DwgTranslator.Core.Tasks.TaskManager)typeof(MainViewModel).GetField("_taskManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var original = (ITaskStore)typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        var resume = typeof(MainViewModel).GetMethod("ResumePendingTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            var path = Path.Combine(AppDataDir, "recovery-notice", "tasks.json");
            var store = new JsonTaskStore(path);
            store.Save(new[] { new TranslationTask("fixture-unfinished.dwg") { Id = "unfinished", Status = TranslationTaskStatus.Translating },
                new TranslationTask("completed.dwg") { Id = "completed", Status = TranslationTaskStatus.Completed },
                new TranslationTask("cancelled.dwg") { Id = "cancelled", Status = TranslationTaskStatus.Cancelled } });
            manager.SwitchAccountStore(store);
            var before = File.ReadAllBytes(path);
            var dialogsBefore = Windows.OfType<PromptDialog>().Count();

            resume.Invoke(vm, null);

            Check(Windows.OfType<PromptDialog>().Count() == dialogsBefore, "startup recovery opens no modal dialog");
            Check(vm.PendingTaskCount == 1 && vm.HasPendingTasks, "unfinished work surfaces as a pending count instead of a dialog");
            Check(vm.PendingTasksNotice.Contains("fixture-unfinished.dwg"), "pending notice names the unfinished drawing");
            Check(manager.Tasks.Count == 3 && manager.Tasks[0].Id == "unfinished", "recovery keeps the in-memory queue intact");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "recovery does not overwrite the persisted queue");
            Check(!manager.IsRunning, "recovery does not auto-start CAD or translation");

            // 取消清除：记录必须原样保留。
            var clear = (System.Windows.Input.ICommand)typeof(MainViewModel).GetProperty("ClearPendingTasksCommand")!.GetValue(vm)!;
            RespondToPrompt("保留记录", () => clear.Execute(null));
            Check(manager.Tasks.Count == 3 && vm.HasPendingTasks, "declining the clear prompt keeps the unfinished record");

            // 确认清除：只删未完成记录，终态历史与图纸文件都不受影响。
            RespondToPrompt("清除记录", () => clear.Execute(null));
            Check(manager.Tasks.Count == 2 && manager.Tasks.All(task => task.Id != "unfinished"), "explicit clear removes only the unfinished task");
            Check(store.Load().Count == 2 && store.Load().All(task => task.Id != "unfinished"), "cleared record is removed from the persisted queue");
            Check(File.Exists(Path.Combine(AppDataDir, "recovery-notice", "tasks.json")), "clearing records never deletes drawing files or the store itself");
            Check(!vm.HasPendingTasks && vm.PendingTaskCount == 0, "pending indicator clears once the records are gone");
            vm.DrawingFiles.Clear();
        }
        finally { manager.SwitchAccountStore(original); vm.DrawingFiles.Clear(); }
    }

    /// <summary>等待 PromptDialog 出现并点击指定按钮；超时即判失败，避免用例静默挂住。</summary>
    private void RespondToPrompt(string buttonLabel, Action trigger)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (_, _) =>
        {
            var dialog = Windows.OfType<PromptDialog>().FirstOrDefault();
            if (dialog == null) { if (DateTime.UtcNow > deadline) { timer.Stop(); failure = new TimeoutException("Prompt dialog missing for " + buttonLabel); } return; }
            timer.Stop();
            try
            {
                var button = FindVisuals<Button>(dialog).SingleOrDefault(b => Equals(b.Content, buttonLabel));
                Check(button != null, "prompt exposes the '" + buttonLabel + "' action");
                button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        };
        timer.Start();
        try { trigger(); } finally { timer.Stop(); }
        if (failure != null) throw failure;
    }
}
