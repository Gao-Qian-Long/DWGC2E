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
            if (request.Method == HttpMethod.Put || request.Method == HttpMethod.Patch)
            {
                Writes++;
                if (WriteGate != null) await WriteGate.Task;
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                LastExpected = doc.RootElement.GetProperty("expected_revision").GetString();
                if (Conflict || LastExpected != Revision) return new(HttpStatusCode.Conflict) {Content=new StringContent("{}")};
                var existing = JsonSerializer.Deserialize<List<JsonElement>>(Entries)!;
                if (request.Method == HttpMethod.Patch) {
                    var upserts=doc.RootElement.GetProperty("upserts").EnumerateArray().Select(e=>e.Clone()).ToList();
                    var deletes=doc.RootElement.GetProperty("delete_ids").EnumerateArray().Select(e=>e.GetString()).ToHashSet();
                    existing.RemoveAll(e=>deletes.Contains(e.GetProperty("id").GetString()) || upserts.Any(u=>u.GetProperty("id").GetString()==e.GetProperty("id").GetString()));
                    existing.AddRange(upserts); Entries=JsonSerializer.Serialize(existing);
                } else Entries = doc.RootElement.GetProperty("entries").GetRawText(); Revision = new string('b',64);
            }
            return new(HttpStatusCode.OK) {Content=new StringContent($"{{\"success\":true,\"revision\":\"{Revision}\",\"entries\":{Entries}}}",Encoding.UTF8,"application/json")};
        }
    }

    private async Task ConfirmCloudDialog(Func<Task> action)
    {
        var timer = new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(25)};
        timer.Tick += (_,_) => {
            var cloud = Windows.OfType<GlossaryCloudWindow>().FirstOrDefault();
            if (cloud != null) {
                FindVisual<DataGrid>(cloud).SelectAll();
                FindVisuals<Button>(cloud).First(b=>Equals(b.Content,"下载所选到本机")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }
            var dialog = Windows.OfType<PromptDialog>().FirstOrDefault();
            if (dialog == null) return;
            var button = FindVisuals<Button>(dialog).FirstOrDefault(b=>Equals(b.Content,"确定") || Equals(b.Content,"采用云端"));
            if (button != null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
        try { await action(); } finally {timer.Stop();}
    }

    private async Task VerifyCloudGlossaryAsync(MainWindow window, MainViewModel vm)
    {
        vm.CurrentPage = MainViewModel.PageGlossary;
        await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
        var glossaryPage = FindVisual<DwgTranslator.App.Views.Pages.GlossaryPage>(window);
        window.UpdateLayout();
        var selectionLabels = new HashSet<string>(StringComparer.Ordinal)
            { "上传所选", "指定方向", "启用", "停用", "修改分类", "导出所选", "删除本机", "取消选择" };
        var selectionButtons = FindVisuals<Button>(glossaryPage)
            .Where(button => button.Content is string label && selectionLabels.Contains(label)).ToArray();
        Check(selectionButtons.Length == selectionLabels.Count
              && selectionButtons.All(button => Math.Abs(button.ActualWidth - 92) < 0.5)
              && selectionButtons.Select(button => button.ActualHeight).Distinct().Count() == 1,
            "glossary selection toolbar actions use one consistent button size");
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
            var originalIds=vm.TermDraft.Select(t=>t.LocalId).ToList();
            await ConfirmCloudDialog(()=>vm.DownloadTermsCommand.ExecuteAsync(null));
            var downloaded=vm.TermDraft.Single(t=>t.CloudId=="12345678-1234-4234-8234-123456789abc");
            Check(downloaded.CloudNote=="网页备注" && !downloaded.Enabled && downloaded.DirectionPending,"legacy cloud download preserves metadata and pending direction");
            Check(originalIds.All(id=>vm.TermDraft.Any(t=>t.LocalId==id)),"selected download preserves unrelated local entries");
            Check(!vm.HasUnsavedTerms,"download merge persists atomically");
            downloaded.Source="改过的原文"; vm.NotifyTermDraftEdited();
            await ConfirmCloudDialog(()=>vm.UploadSelectedTermsAsync(new[]{downloaded}));
            Check(handler.Writes==1 && handler.LastExpected==new string('a',64),"selected PATCH includes observed revision");
            Check(vm.TermFeedback.Contains("已核对") && !vm.HasUnsavedTerms,"cloud success persists association");
            handler.Conflict=true;
            downloaded.Target="local change";
            await ConfirmCloudDialog(()=>vm.UploadSelectedTermsAsync(new[]{downloaded}));
            Check(vm.TermFeedback.Contains("未覆盖") && downloaded.Target=="local change","cloud conflict preserves local content");
            handler.Offline=true;
            await ConfirmCloudDialog(()=>vm.DownloadTermsCommand.ExecuteAsync(null));
            Check(vm.TermDraft.Any(t=>t.Target=="local change") && !vm.IsCloudGlossarySyncing,"failed download preserves local content and unlocks UI");
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
            Check(vm.TermDraft.Any(t=>t.Target=="local change"),"account change discards in-flight cloud download");
            Capture(window,"glossary-cloud-verified");
            versionField.SetValue(vm, originalVersion);

        }
        finally
        {
            clientField.SetValue(vm,originalClient); verifiedField.SetValue(vm,originalVerified); versionField.SetValue(vm,originalVersion);
            vm.IsCloudGlossarySyncing=false;
        }
    }
}
