using System.IO;
using System.Reflection;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Tasks;
namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyAccountSaveFailureAsync(MainViewModel vm, bool logout)
    {
        var manager = (DwgTranslator.Core.Tasks.TaskManager)typeof(MainViewModel).GetField("_taskManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var store = (JsonTaskStore)typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        var task = manager.Enqueue(Path.Combine(AppDataDir, "save-failure-probe.dwg"));
        var settingsPath = Path.Combine(AppDataDir, "settings.json");
        var settings = File.ReadAllBytes(settingsPath);
        var calls = api.LoginCalls;
        var logoutCalls = api.LogoutCalls;
        var signedIn = vm.IsAccountLoggedIn;
        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (logout) await vm.LogoutAccountCommand.ExecuteAsync(null);
            else await vm.SubmitLoginAsync("not-a-real-password");
            Check(api.LogoutCalls == logoutCalls, "failed preflight prevents remote logout");
            Check(api.LoginCalls == calls, "failed task save prevents login request");
            Check(vm.IsAccountLoggedIn == signedIn, "failed task save preserves current authentication state");
            Check(manager.Tasks.Any(t => ReferenceEquals(t, task)), "failed account transition preserves in-memory queue");
            Check(File.ReadAllBytes(settingsPath).SequenceEqual(settings), "failed account transition does not persist another session");
            Check(vm.AccountFeedback.Contains("保存"), "failed account transition gives persistence feedback");
        }
        manager.Clear(includeUnfinished: true);
        Check(!store.LastSaveFailed, "task save recovers after account-switch lock is released");
    }
}
