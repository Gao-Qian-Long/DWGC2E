using System.IO;
using System.Reflection;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;

namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyExpiredProofreadingAsync(MainViewModel vm)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var manager = (DwgTranslator.Core.Tasks.TaskManager)typeof(MainViewModel).GetField("_taskManager", flags)!.GetValue(vm)!;
        var version = typeof(MainViewModel).GetField("_sessionVersion", flags)!;
        var path = Path.Combine(AppDataDir, "settings.json");
        foreach (var lockSettings in new[] { false, true })
        {
            await WaitUntil(() => !vm.IsAccountRefreshing && !vm.IsGlossaryLoading, "expiry guard ready");
            Check(vm.IsAccountLoggedIn, "expiry fixture starts authenticated");
            var before = File.ReadAllBytes(path);
            var owner = SettingsStore.Read(path).ActiveAccountId;
            var previousVersion = (int)version.GetValue(vm)!;
            var store = typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", flags)!.GetValue(manager);
            var entity = new TextEntity { Handle = "expiry-proof", PlainText = "fixture", TranslatedText = "original" };
            vm.Entities.Add(entity);
            vm.TrackProofreadingEdit(entity);
            entity.TranslatedText = "unsaved expiry edit";
            FileStream? locked = null;
            try
            {
                if (lockSettings) locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                api.Expired = true;
                await vm.RefreshAccountCommand.ExecuteAsync(null);
                Check(!vm.IsAccountLoggedIn && !vm.HasSavedAccountSession && vm.AccountState == AccountSessionState.Expired, "expiry removes authentication even when settings locked=" + lockSettings);
                Check((int)version.GetValue(vm)! > previousVersion, "expiry invalidates outstanding session callbacks");
                Check(vm.Entities.Contains(entity) && entity.TranslatedText == "unsaved expiry edit" && vm.HasUnsavedProofreading, "expiry preserves dirty proofreading");
                Check(ReferenceEquals(store, typeof(DwgTranslator.Core.Tasks.TaskManager).GetField("_store", flags)!.GetValue(manager)), "expiry retains owning task store");
                Check(vm.OnlineProfile == null && vm.OnlineUsage == null && vm.OnlineSubscription == null && vm.OnlineDevices.Count == 0, "expiry clears account entitlement display");
                Check(vm.AccountFeedback.Contains("校对"), "expiry explains retained edits");
            }
            finally { locked?.Dispose(); api.Expired = false; }
            var after = SettingsStore.Read(path);
            Check(after.ActiveAccountId == owner, "expiry does not reassign edits to guest workspace");
            if (lockSettings) Check(File.ReadAllBytes(path).SequenceEqual(before), "failed credential persistence preserves original settings bytes");
            else Check(string.IsNullOrEmpty(AppConfig.DecryptApiKey(after.AuthTokenEncrypted)), "expiry persists credential removal");
            if (lockSettings)
            {
                await WaitUntil(() => string.IsNullOrEmpty(SettingsStore.Read(path).AuthTokenEncrypted), "locked credential automatically cleared after release");
                Check(SettingsStore.Read(path).ActiveAccountId == owner && vm.HasUnsavedProofreading, "retry only clears credential, not owner or dirty edit");
            }
            vm.DiscardProofreadingCommand.Execute(null);
            vm.Entities.Remove(entity);
            await vm.LogoutAccountCommand.ExecuteAsync(null);
            await vm.SubmitLoginAsync("fixture-not-real-password");
            Check(vm.IsAccountLoggedIn, "explicit login recovers after retained expiry workspace");
        }
        foreach (var kind in new[] { "terms", "settings", "clean" })
        {
            await WaitUntil(() => !vm.IsAccountRefreshing && !vm.IsGlossaryLoading, "draft expiry ready");
            var owner = SettingsStore.Read(path).ActiveAccountId;
            if (kind == "terms") { vm.LoadTermEditor(); vm.AddTermCommand.Execute(null); vm.SelectedTerm!.Source = "unsaved-expiry-term"; }
            if (kind == "settings") vm.SettingsDraft.ExportDirectory = "unsaved-expiry-output";
            Check(!vm.HasUnsavedProofreading, "draft fixture does not rely on proofreading dirty flag");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                api.Expired = true;
                try { await vm.RefreshAccountCommand.ExecuteAsync(null); }
                finally { api.Expired = false; }
                Check(!vm.HasSavedAccountSession && !vm.IsAccountLoggedIn, "locked expiry revokes memory session: " + kind);
                Check(SettingsStore.Read(path).ActiveAccountId == owner, "locked expiry retains workspace owner: " + kind);
                if (kind == "terms") Check(vm.HasUnsavedTerms && vm.TermDraft.Any(t => t.Source == "unsaved-expiry-term"), "expiry preserves terms-only draft");
                if (kind == "settings") Check(vm.HasUnsavedSettings && vm.SettingsDraft.ExportDirectory == "unsaved-expiry-output", "expiry preserves settings-only draft");
            }
            if (kind == "clean")
            {
                // Another login writes a different credential before the scheduled retry.
                SettingsStore.Update(path, c => c.AuthTokenEncrypted = AppConfig.EncryptApiKey("replacement-fixture"));
                var replacement = File.ReadAllBytes(path);
                await WaitUntil(() => typeof(MainViewModel).GetField("_pendingRejectedCredential", flags)!.GetValue(vm) == null, "retry notices replacement credential");
                Check(File.ReadAllBytes(path).SequenceEqual(replacement), "retry does not rewrite newer credential or settings bytes");
            }
            else
                await WaitUntil(() => string.IsNullOrEmpty(SettingsStore.Read(path).AuthTokenEncrypted), "draft expiry retry clears token");
            vm.DiscardTermsCommand.Execute(null);
            vm.DiscardSettingsChangesCommand.Execute(null);
            await vm.LogoutAccountCommand.ExecuteAsync(null);
            await vm.SubmitLoginAsync("fixture-not-real-password");
            Check(vm.IsAccountLoggedIn, "login recovers after draft expiry: " + kind);
        }
    }
}
