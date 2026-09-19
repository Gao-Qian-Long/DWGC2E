using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ACadSharp;
using ACadSharp.IO;
using DwgTranslator.App;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyProofreadingPersistenceAsync(MainViewModel vm)
    {
        await WaitUntil(() => !vm.IsAccountRefreshing && !vm.IsGlossaryLoading, "proofreading persistence fixture ready");
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var config = (AppConfig)typeof(MainViewModel).GetField("_config", flags)!.GetValue(vm)!;
        var originalOwner = config.ActiveAccountId;
        var originalToken = AppConfig.DecryptApiKey(config.AuthTokenEncrypted);
        var switchMethod = typeof(MainViewModel).GetMethod("SwitchAccountWorkspaceAsync", flags)!;
        Task Switch(string token, string account) => (Task)switchMethod.Invoke(vm, new object[] { token, account })!;
        var owner = "proofreading-fixture-A";
        var store = new ProofreadingStore(Path.Combine(AccountWorkspace.DirectoryFor(AppDataDir, owner), "proofreading.json"));
        var fixture = Path.Combine(AppDataDir, "proofreading-fixture"); Directory.CreateDirectory(fixture);
        var sources = new[] { Path.Combine(fixture, "a.dwg"), Path.Combine(fixture, "b.dwg") };
        foreach (var source in sources)
        {
            var document = new CadDocument();
            document.Entities.Add(new ACadSharp.Entities.MText { Value = "阀门反馈", Height = 3.5, RectangleWidth = 30 });
            DwgWriter.Write(source, document);
        }
        var loginCalls = api.LoginCalls; var logoutCalls = api.LogoutCalls;
        try
        {
            await Switch("fixture-local-token", owner);
            var entries = sources.SelectMany(p => new DwgReaderService().ExtractFromFile(p)).ToArray();
            foreach (var entity in entries) { vm.Entities.Add(entity); vm.TrackProofreadingEdit(entity); entity.TranslatedText = Path.GetFileNameWithoutExtension(entity.SourceFilePath) + " reviewed"; }
            vm.SaveProofreadingCommand.Execute(null);
            Check(!vm.HasUnsavedProofreading && File.Exists(store.FilePath), "proofreading save commits account-local record before clearing dirty");
            Check(entries.All(e => e.Status == TranslationStatus.Reviewed), "proofreading durable save marks reviewed");
            var before = File.ReadAllBytes(store.FilePath);
            vm.TrackProofreadingEdit(entries[0]); entries[0].TranslatedText = "";
            using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                vm.SaveProofreadingCommand.Execute(null);
                Check(vm.HasUnsavedProofreading && entries[0].TranslatedText == "" && entries[0].Status == TranslationStatus.Reviewed, "failed proofreading save retains edit, original status and dirty flag");
                Check(vm.StatusMessage.Contains("失败") && before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "failed proofreading save preserves previous bytes and reports failure");
                var dialogSeen = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                timer.Tick += (_, _) => {
                    var dialog = Windows.OfType<PromptDialog>().FirstOrDefault(w => w.Title == "未保存的校对");
                    if (dialog == null) return;
                    dialogSeen = true; timer.Stop();
                    FindVisuals<Button>(dialog).Single(b => Equals(b.Content, "保存更改")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                timer.Start();
                try { await vm.LogoutAccountCommand.ExecuteAsync(null); } finally { timer.Stop(); }
                Check(dialogSeen && config.ActiveAccountId == owner && vm.Entities.Contains(entries[0]) && vm.HasUnsavedProofreading, "Save choice cannot leave account when proofreading commit fails");
                Check(api.LoginCalls == loginCalls && api.LogoutCalls == logoutCalls, "failed proofreading save prevents remote authentication actions");
                var clear = (bool)typeof(MainViewModel).GetMethod("TryClearSavedProofreading", flags)!.Invoke(vm, null)!;
                Check(!clear && vm.Entities.Contains(entries[0]), "locked proofreading clear preserves workspace");
            }
            vm.SaveProofreadingCommand.Execute(null);
            Check(!vm.HasUnsavedProofreading && entries[0].Status == TranslationStatus.Pending, "retry saves intentionally blank translation as pending");
            await Switch("fixture-other-token", "proofreading-fixture-B");
            Check(vm.Entities.Count == 0, "other account cannot see saved proofreading");
            await Switch("fixture-local-token", owner);
            Check(vm.Entities.Count == 2 && vm.Entities.Single(e => e.SourceFilePath == sources[0]).TranslatedText == "" && vm.Entities.Single(e => e.SourceFilePath == sources[1]).TranslatedText == "b reviewed", "switching back restores both drawings without cross-handle contamination");
            Check(!vm.HasUnsavedProofreading && vm.DrawingFiles.Count == 2 && vm.HasDrawingFiles, "restored proofreading is clean and visible in drawing rows");

            // A fresh DI graph and ViewModel exercise the real startup hook, not just a store read.
            var services = new ServiceCollection();
            typeof(App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { services });
            services.AddSingleton<IApiClient>(api);
            using (var provider = services.BuildServiceProvider())
            using (var restarted = provider.GetRequiredService<MainViewModel>())
            {
                await restarted.InitializeAsync();
                await WaitUntil(() => !restarted.IsAccountRefreshing, "fresh proofreading viewmodel account refresh");
                Check(restarted.Entities.Count == 2 && restarted.Entities.Single(e => e.SourceFilePath == sources[1]).TranslatedText == "b reviewed", "fresh application services and startup restore saved proofreading");
                Check(restarted.Entities.All(e => e.MTextRectangleWidth == 30 && e.Height == 3.5), "proofreading restart uses current drawing geometry");
            }
            store.Clear();
            await Switch("fixture-other-token", "proofreading-fixture-B"); await Switch("fixture-local-token", owner);
            Check(vm.Entities.Count == 0, "cleared saved proofreading does not reappear on account return");
        }
        finally
        {
            await ConfirmModalsAsync(() => vm.DiscardProofreadingCommand.Execute(null), "proofreading persistence cleanup");
            store.Clear();
            await Switch(originalToken, originalOwner);
        }
    }
}
