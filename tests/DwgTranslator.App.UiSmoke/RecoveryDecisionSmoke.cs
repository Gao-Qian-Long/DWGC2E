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
    private void VerifyRecoveryDecision(MainViewModel vm)
    {
        var manager = (DwgTranslator.Core.Tasks.TaskManager)typeof(MainViewModel).GetField("_taskManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var original = (ITaskStore)typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        var resume = typeof(MainViewModel).GetMethod("ResumePendingTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            foreach (var action in new[] { "close", "defer", "clear" })
            {
                var path = Path.Combine(AppDataDir, "recovery-decision-" + action, "tasks.json");
                var store = new JsonTaskStore(path);
                store.Save(new[] { new TranslationTask("fixture-" + action + ".dwg") { Id = action, Status = TranslationTaskStatus.Translating },
                    new TranslationTask("completed.dwg") { Id = "completed", Status = TranslationTaskStatus.Completed },
                    new TranslationTask("cancelled.dwg") { Id = "cancelled", Status = TranslationTaskStatus.Cancelled } });
                manager.SwitchAccountStore(store);
                var before = File.ReadAllBytes(path);
                Exception? failure = null;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                var deadline = DateTime.UtcNow.AddSeconds(5);
                timer.Tick += (_, _) =>
                {
                    var dialog = Windows.OfType<PromptDialog>().FirstOrDefault();
                    if (dialog == null) { if (DateTime.UtcNow > deadline) { timer.Stop(); failure = new TimeoutException("Recovery dialog missing"); } return; }
                    timer.Stop();
                    try
                    {
                        var buttons = FindVisuals<Button>(dialog).ToArray();
                        foreach (var label in new[] { "继续处理", "清除未完成记录", "暂不处理" })
                            Check(buttons.Count(b => Equals(b.Content, label)) == 1, "recovery action label " + label);
                        if (action == "close") dialog.Close();
                        else buttons.Single(b => Equals(b.Content, action == "defer" ? "暂不处理" : "清除未完成记录"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                };
                timer.Start();
                try { resume.Invoke(vm, null); } finally { timer.Stop(); }
                if (failure != null) throw failure;
                if (action == "clear") Check(manager.Tasks.Count == 2 && store.Load().Count == 2 && manager.Tasks.All(task => task.Id != action), "explicit clear removes only unfinished task and preserves terminal history");
                else
                {
                    Check(manager.Tasks.Count == 3 && manager.Tasks[0].Id == action, "recovery " + action + " preserves in-memory queue");
                    Check(File.ReadAllBytes(path).SequenceEqual(before), "recovery " + action + " does not overwrite persisted queue");
                    Check(!manager.IsRunning, "recovery " + action + " does not auto-start CAD or translation");
                }
                vm.DrawingFiles.Clear();
            }
        }
        finally { manager.SwitchAccountStore(original); vm.DrawingFiles.Clear(); }
    }
}
