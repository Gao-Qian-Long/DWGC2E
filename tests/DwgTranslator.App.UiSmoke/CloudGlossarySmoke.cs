using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.Core.Api;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private sealed class GlossaryHandler : HttpMessageHandler
    {
        public string Revision = new('a',64);
        public string Entries = "[{\"id\":\"12345678-1234-4234-8234-123456789abc\",\"source\":\"云端原文\",\"target\":\"Cloud\",\"note\":\"网页备注\",\"enabled\":false}]";
        public bool Conflict, Offline;
        public int Writes;
        public string? LastExpected;
        public TaskCompletionSource? ReadGate, WriteGate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("isolated network failure");
            if (request.Method == HttpMethod.Get && ReadGate != null) await ReadGate.Task;
            if (request.Method == HttpMethod.Put)
            {
                Writes++;
                if (WriteGate != null) await WriteGate.Task;
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                LastExpected = doc.RootElement.GetProperty("expected_revision").GetString();
                if (Conflict || LastExpected != Revision) return new(HttpStatusCode.Conflict) {Content=new StringContent("{}")};
                Entries = doc.RootElement.GetProperty("entries").GetRawText(); Revision = new string('b',64);
            }
            return new(HttpStatusCode.OK) {Content=new StringContent($"{{\"success\":true,\"revision\":\"{Revision}\",\"entries\":{Entries}}}",Encoding.UTF8,"application/json")};
        }
    }

    private async Task ConfirmCloudDialog(Func<Task> action)
    {
        var timer = new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(25)};
        timer.Tick += (_,_) => {
            var dialog = Windows.OfType<PromptDialog>().FirstOrDefault();
            if (dialog == null) return;
            var button = FindVisuals<Button>(dialog).FirstOrDefault(b=>Equals(b.Content,"确定"));
            if (button != null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
        try { await action(); } finally {timer.Stop();}
    }

    private async Task VerifyCloudGlossaryAsync(MainWindow window, MainViewModel vm)
    {
        vm.CurrentPage = MainViewModel.PageGlossary;
        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        var type = typeof(MainViewModel);
        var clientField = type.GetField("_apiClient",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var verifiedField = type.GetField("_sessionVerified",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var versionField = type.GetField("_sessionVersion",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var originalClient = clientField.GetValue(vm); var originalVerified = verifiedField.GetValue(vm);
        var originalVersion = versionField.GetValue(vm);
        var handler = new GlossaryHandler(); using var http = new HttpClient(handler);
        try
        {
            clientField.SetValue(vm,new WorkerApiClient(http,"https://isolated.invalid",()=>"isolated-session","d","h"));
            verifiedField.SetValue(vm,true);
            await ConfirmCloudDialog(()=>vm.DownloadTermsCommand.ExecuteAsync(null));
            Check(vm.TermDraft.Count==1 && vm.TermDraft[0].CloudNote=="网页备注" && !vm.TermDraft[0].Enabled,"cloud download preserves identity note and disabled status");
            Check(vm.HasUnsavedTerms && vm.SaveTermEditor(),"cloud download is explicitly saved locally");
            vm.TermDraft[0].Source="改过的原文"; vm.NotifyTermDraftEdited();
            await ConfirmCloudDialog(()=>vm.UploadTermsCommand.ExecuteAsync(null));
            Check(handler.Writes==1 && handler.LastExpected==new string('a',64),"APP upload includes observed revision");
            Check(vm.TermFeedback.Contains("已保存到云端") && !vm.HasUnsavedTerms,"APP cloud success saves latest local state");
            Check(vm.GlossaryEntries.Single().CloudNote=="网页备注" && vm.GlossaryEntries.Single().CloudId!=null,"renamed term retains cloud metadata after local save");
            handler.Conflict=true;
            vm.TermDraft[0].Target="local change";
            await ConfirmCloudDialog(()=>vm.UploadTermsCommand.ExecuteAsync(null));
            Check(vm.TermFeedback.Contains("未覆盖") && vm.TermDraft[0].Target=="local change","cloud conflict preserves local content");
            await ConfirmCloudDialog(()=>vm.UploadTermsCommand.ExecuteAsync(null));
            Check(handler.LastExpected==new string('b',64) && handler.Writes==3,"conflict retry never silently refreshes revision");
            handler.Offline=true;
            await ConfirmCloudDialog(()=>vm.DownloadTermsCommand.ExecuteAsync(null));
            Check(vm.TermDraft[0].Target=="local change" && !vm.IsCloudGlossarySyncing,"failed download preserves local content and unlocks UI");
            handler.Offline=false;
            handler.ReadGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read=ConfirmCloudDialog(()=>vm.DownloadTermsCommand.ExecuteAsync(null));
            await WaitUntil(()=>vm.IsCloudGlossarySyncing,"cloud read pending");
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var page=FindVisual<DwgTranslator.App.Views.Pages.GlossaryPage>(window);
            Check(!((FrameworkElement)page.FindName("TermList")).IsEnabled && !vm.SaveTermEditor(),"pending sync disables editor and local save");
            AssertLanguageSwitchBlocked(vm, "pending download");
            versionField.SetValue(vm,(int)originalVersion!+1);
            handler.ReadGate.SetResult(); await read;
            Check(vm.TermDraft[0].Target=="local change","account change discards in-flight cloud download");
            Capture(window,"glossary-cloud-verified");
            versionField.SetValue(vm, originalVersion);
            await VerifyMergeDialogAsync(window, vm, handler, versionField);
        }
        finally
        {
            clientField.SetValue(vm,originalClient); verifiedField.SetValue(vm,originalVerified); versionField.SetValue(vm,originalVersion);
            vm.IsCloudGlossarySyncing=false;
        }
    }
}
