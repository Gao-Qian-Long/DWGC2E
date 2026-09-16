using System.IO;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.Core.Models;

namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyProofreadingAccountCancelAsync(MainViewModel vm)
    {
        await WaitUntil(() => !vm.IsAccountRefreshing && !vm.IsGlossaryLoading, "account guard fixture ready");
        var entity = new TextEntity { Handle = "account-cancel-proof", PlainText = "fixture", TranslatedText = "original" };
        var settings = Path.Combine(AppDataDir, "settings.json");
        var before = File.ReadAllBytes(settings);
        var loginName = vm.LoginName;
        vm.LoginName = "isolated-guard-fixture";
        vm.Entities.Add(entity);
        vm.TrackProofreadingEdit(entity);
        entity.TranslatedText = "unsaved edit";
        try
        {
            foreach (var logout in new[] { false, true })
            {
                var loginCalls = api.LoginCalls;
                var logoutCalls = api.LogoutCalls;
                var signedIn = vm.IsAccountLoggedIn;
                var dialogSeen = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                timer.Tick += (_, _) =>
                {
                    var dialog = Windows.OfType<PromptDialog>().FirstOrDefault();
                    if (dialog == null) return;
                    dialogSeen = dialog.Title == "未保存的校对";
                    timer.Stop();
                    dialog.Close(); // Window close is Cancel, never Save or Discard.
                };
                timer.Start();
                try
                {
                    if (logout) await vm.LogoutAccountCommand.ExecuteAsync(null);
                    else await vm.SubmitLoginAsync("fixture-not-real-password");
                }
                finally { timer.Stop(); }
                var label = logout ? "logout" : "login";
                Check(dialogSeen, label + " asks about unsaved proofreading");
                Check(api.LoginCalls == loginCalls && api.LogoutCalls == logoutCalls, label + " cancel makes no authentication request");
                Check(vm.Entities.Contains(entity) && entity.TranslatedText == "unsaved edit" && vm.HasUnsavedProofreading, label + " cancel preserves unsaved entity and dirty state");
                Check(vm.IsAccountLoggedIn == signedIn && File.ReadAllBytes(settings).SequenceEqual(before), label + " cancel preserves session and persisted settings");
            }
        }
        finally
        {
            vm.DiscardProofreadingCommand.Execute(null);
            vm.Entities.Remove(entity);
            vm.LoginName = loginName;
        }
    }
}
