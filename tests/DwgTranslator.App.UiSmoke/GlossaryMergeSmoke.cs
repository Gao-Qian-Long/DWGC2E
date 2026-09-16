using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private async Task DriveMergeDialog(MainViewModel vm, Action<GlossaryMergeDialog> interact)
    {
        Exception? failure = null; var handled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        var started = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            var dialog = FindVisuals<GlossaryMergeDialog>(MainWindow).FirstOrDefault();
            if (dialog == null || !dialog.IsLoaded) return;
            dialog.UpdateLayout();
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(8)) { failure ??= new Exception("merge dialog timed out"); dialog.Close(); return; }
            if (handled) return; handled = true;
            try { interact(dialog); } catch (Exception ex) { failure = ex; dialog.Close(); }
        };
        timer.Start();
        try { await vm.MergeTermsCommand.ExecuteAsync(null); }
        finally { timer.Stop(); }
        if (failure != null) throw failure;
        Check(handled, "APP merge preview opened");
    }

    private static void ConfirmMerge(GlossaryMergeDialog dialog) => FindVisuals<Button>(dialog)
        .Single(x => Equals(x.Content, "确认合并并保存云端")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void AssertLanguageSwitchBlocked(MainViewModel vm, string phase)
    {
        var source = vm.CurrentSourceLang; var target = vm.CurrentTargetLang;
        vm.CurrentSourceLang = source == "DE" ? "FR" : "DE";
        Check(vm.CurrentSourceLang == source && vm.CurrentTargetLang == target, phase + " blocks source-language switch");
        vm.CurrentTargetLang = target == "JA" ? "KO" : "JA";
        Check(vm.CurrentSourceLang == source && vm.CurrentTargetLang == target, phase + " blocks target-language switch");
        Check(vm.TermFeedback.Contains("同步云端"), phase + " explains why language switch is blocked");
    }
    private async Task VerifyMergeDialogAsync(MainWindow window, MainViewModel vm, GlossaryHandler handler, FieldInfo versionField)
    {
        handler.Conflict = false; handler.ReadGate = null;
        handler.Entries = handler.Entries.Replace("Cloud", "Remote"); handler.Revision = new string('c', 64);
        var before = handler.Writes;
        await DriveMergeDialog(vm, dialog =>
        {
            AssertLanguageSwitchBlocked(vm, "merge preview");
            var choices = FindVisuals<ComboBox>(dialog).ToList();
            Console.WriteLine("MERGE_CHOICES="+choices.Count+":"+string.Join(",",choices.Select(c=>c.SelectedIndex)));
            Check(choices.Count == 1 && choices[0].SelectedIndex == -1, "APP conflicting row requires explicit selection");
            ConfirmMerge(dialog);
            Check(dialog.IsVisible && handler.Writes == before, "unresolved merge cannot write cloud");
            Capture(dialog, "glossary-merge-conflict");
            dialog.Close();
        });
        Check(handler.Writes == before && vm.TermDraft[0].Target == "local change", "merge cancellation preserves draft and cloud");
        vm.TermDraft[0].HitCount = 7;
        var hitAt = DateTime.UtcNow.AddDays(-1); vm.TermDraft[0].LastHitAt = hitAt;
        var sourceKind = vm.TermDraft[0].SourceKind;
        await DriveMergeDialog(vm, dialog =>
        {
            FindVisuals<ComboBox>(dialog).Single().SelectedIndex = 0;
            handler.Conflict = true; ConfirmMerge(dialog);
        });
        Check(vm.TermFeedback.Contains("未覆盖") && vm.TermDraft[0].Target == "local change", "new cloud race preserves APP draft");
        Check(handler.LastExpected == new string('c', 64), "APP merge uses preview revision not stale upload revision");
        handler.Conflict = false;
        await DriveMergeDialog(vm, dialog =>
        {
            FindVisuals<ComboBox>(dialog).Single().SelectedIndex = 0; ConfirmMerge(dialog);
        });
        Check(vm.TermFeedback.Contains("合并已保存") && !vm.HasUnsavedTerms, "APP selected merge saves cloud and latest local glossary");
        Check(vm.TermDraft[0].HitCount == 7 && vm.TermDraft[0].LastHitAt == hitAt && vm.TermDraft[0].SourceKind == sourceKind,
            "merge preserves local-only metadata");
        // A cloud-only addition and a local edit are both retained, without extra snapshots.
        handler.Entries = handler.Entries.TrimEnd(']') + ",{\"id\":\"22345678-1234-4234-8234-123456789abc\",\"source\":\"云端新增\",\"target\":\"Added\",\"enabled\":true}]";
        handler.Revision = new string('d', 64); vm.TermDraft[0].Target = "Another local edit";
        await DriveMergeDialog(vm, dialog =>
        {
            Check(FindVisuals<ComboBox>(dialog).All(x => x.SelectedIndex >= 0), "independent changes have safe defaults"); ConfirmMerge(dialog);
        });
        Check(vm.TermDraft.Count == 2 && vm.TermDraft[0].Target == "Another local edit" && vm.TermDraft.Any(x => x.Source == "云端新增"), "independent APP and cloud changes both survive merge");
        handler.WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = handler.Writes;
        var saving = DriveMergeDialog(vm, ConfirmMerge);
        try
        {
            await WaitUntil(() => handler.Writes == writes + 1, "merge save reaches isolated server");
            AssertLanguageSwitchBlocked(vm, "pending merge save");
        }
        finally { handler.WriteGate.SetResult(); await saving; handler.WriteGate = null; }
        Check(!vm.HasUnsavedTerms, "merge save completes in original language");
        var originalSource = vm.CurrentSourceLang; var originalTarget = vm.CurrentTargetLang;
        try
        {
            vm.CurrentTargetLang = originalTarget == "JA" ? "KO" : "JA";
            await WaitUntil(() => !vm.IsGlossaryLoading, "new target glossary loaded");
            Check(vm.CurrentTargetLang != originalTarget, "idle target-language switch succeeds");
            var cloudBefore = handler.Entries; var countBefore = handler.Writes;
            await DriveMergeDialog(vm, dialog =>
            {
                Check(FindVisuals<ComboBox>(dialog).Any(x => x.SelectedIndex == 1), "new language does not treat old basis as cloud deletion");
                dialog.Close();
            });
            Check(handler.Writes == countBefore && handler.Entries == cloudBefore, "new-language merge cancellation never changes cloud");
            vm.CurrentSourceLang = originalSource == "DE" ? "FR" : "DE";
            await WaitUntil(() => !vm.IsGlossaryLoading, "new source glossary loaded");
            Check(vm.CurrentSourceLang != originalSource, "idle source-language switch succeeds");
        }
        finally
        {
            vm.CurrentSourceLang = originalSource;
            await WaitUntil(() => !vm.IsGlossaryLoading, "restore source glossary");
            vm.CurrentTargetLang = originalTarget;
            await WaitUntil(() => !vm.IsGlossaryLoading, "restore target glossary");
        }
        before = handler.Writes;
        await DriveMergeDialog(vm, _ => versionField.SetValue(vm, (int)versionField.GetValue(vm)! + 1));
        Check(handler.Writes == before && !vm.IsCloudGlossarySyncing, "account switch automatically closes merge and prevents save");
        Capture(window, "glossary-merge-verified");
    }
}
