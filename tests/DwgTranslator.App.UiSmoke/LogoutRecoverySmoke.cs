using System.IO;
using System.Reflection;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Tasks;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Models;
using System.Text.Json;
namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyLogoutRecoveryAsync(MainViewModel vm)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var manager = (DwgTranslator.Core.Tasks.TaskManager)typeof(MainViewModel).GetField("_taskManager", flags)!.GetValue(vm)!;
        var store = (JsonTaskStore)typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", flags)!.GetValue(manager)!;
        var version = typeof(MainViewModel).GetField("_sessionVersion", flags)!;
        var task = manager.Enqueue(Path.Combine(AppDataDir, "logout-recovery-probe.dwg"));
        var settingsPath = Path.Combine(AppDataDir, "settings.json");
        var original = File.ReadAllBytes(settingsPath);
        foreach (var throws in new[] { false, true })
        {
            api.OnLogout = () => throws ? throw new IOException("controlled network failure") : false;
            await vm.LogoutAccountCommand.ExecuteAsync(null);
            Check(vm.IsAccountLoggedIn && vm.AccountFeedback.Contains("尚未确认"), "unconfirmed logout keeps authentication and reports uncertainty");
            Check(File.ReadAllBytes(settingsPath).SequenceEqual(original), "unconfirmed logout preserves settings");
        }
        foreach (var lockSettings in new[] { true, false })
        {
            var previousVersion = (int)version.GetValue(vm)!;
            FileStream? locked = null;
            api.OnLogout = () =>
            {
                locked = new FileStream(lockSettings ? settingsPath : store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            };
            try
            {
                await vm.LogoutAccountCommand.ExecuteAsync(null);
                Check(!vm.IsAccountLoggedIn && vm.AccountState == AccountSessionState.SignedOut, "confirmed remote logout removes local authentication despite persistence failure");
                Check((int)version.GetValue(vm)! > previousVersion, "confirmed logout invalidates outstanding account callbacks");
                Check(vm.OnlineProfile == null && vm.OnlineSubscription == null && vm.OnlineUsage == null && vm.OnlineDevices.Count == 0, "confirmed logout clears account and entitlement display");
                Check(manager.Tasks.Any(t => ReferenceEquals(t, task)), "post-revocation persistence failure preserves queued tasks");
                Check(vm.AccountFeedback.Contains("云端已退出") && vm.AccountFeedback.Contains("任务已保留"), "post-revocation failure explains revoked session and retained tasks");
            }
            finally { locked?.Dispose(); api.OnLogout = null; }
            if (lockSettings)
            {
                Check(File.ReadAllBytes(settingsPath).SequenceEqual(original), "locked settings remain unchanged until retry");
                await WaitUntil(() => string.IsNullOrEmpty(SettingsStore.Read(settingsPath).AuthTokenEncrypted), "logout credential cleanup retries after unlock");
            }
            var retained = SettingsStore.Read(settingsPath);
            var prior = JsonSerializer.Deserialize<AppConfig>(original, AppConfigJson.ReadOptions)!;
            Check(retained.ActiveAccountId == prior.ActiveAccountId && retained.ExportDirectory == prior.ExportDirectory, "credential cleanup preserves account owner and output configuration for recovery");
            Check(string.IsNullOrEmpty(retained.AuthTokenEncrypted) && !vm.HasSavedAccountSession, "revoked logout credential is cleared independently of task-store failure");
            await vm.LogoutAccountCommand.ExecuteAsync(null);
            Check(!vm.HasSavedAccountSession && !vm.IsAccountLoggedIn, "logout retry completes guest session persistence");
            await vm.SubmitLoginAsync("not-a-real-password");
            Check(vm.IsAccountLoggedIn, "login recovers after logout cleanup retry");
            store = (JsonTaskStore)typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", flags)!.GetValue(manager)!;
            task = manager.Tasks.Single(t => t.FilePath.EndsWith("logout-recovery-probe.dwg"));
            original = File.ReadAllBytes(settingsPath);
        }
        manager.Clear(includeUnfinished: true);
    }
}
