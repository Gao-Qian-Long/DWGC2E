using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Text.Json;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task MergeTermsAsync()
    {
        if (IsCloudGlossarySyncing || IsGlossaryLoading || IsProcessing || !RequireAccount()) return;
        if (_apiClient is not ICloudGlossaryClient cloud) { TermFeedback = "当前连接不支持安全云端同步。"; return; }
        var context = CloudGlossaryContext;
        var draft = JsonSerializer.Serialize(TermDraft);
        var local = TermDraft.Select(t => t.Clone()).ToList();
        bool Active() => context == CloudGlossaryContext && draft == JsonSerializer.Serialize(TermDraft);
        IsCloudGlossarySyncing = true;
        TermFeedback = "正在读取最新云端内容并比较……";
        try
        {
            if (_cloudGlossaryContext != context) { _cloudGlossaryBasis = null; _cloudGlossaryContext = context; }
            var latest = await cloud.ReadCloudGlossaryAsync();
            if (!Active()) return;
            var entries = local.Select(t => new CloudGlossaryEntry { Id = t.CloudId, Note = t.CloudNote,
                Source = t.Source.Trim(), Target = t.Target.Trim(), Category = t.Category, Folder = t.Folder, Enabled = t.Enabled }).ToList();
            // Without an observed common basis, absent local rows are NOT treated as cloud deletions.
            var rows = CloudGlossaryMerge.Plan(_cloudGlossaryBasis?.Entries ?? Array.Empty<CloudGlossaryEntry>(), entries, latest.Entries);
            var choices = await Views.GlossaryMergeDialog.ShowAsync(rows, Active);
            if (!Active()) return;
            if (choices == null) { TermFeedback = "已取消合并，本机草稿保留。"; return; }
            var merged = CloudGlossaryMerge.Resolve(rows, choices);
            // Capture local-only metadata per chosen row before publishing. Server entries are keyed by unique source/target.
            var metadata = new Dictionary<(string, string), GlossaryEntry>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; var choice = choices.TryGetValue(i, out var value) ? value : row.SuggestedChoice;
                var selected = choice == GlossaryMergeChoice.Local ? row.Local : row.Remote;
                if (selected == null || row.Local == null) continue;
                var previous = local.FirstOrDefault(t => row.Local.Id != null && t.CloudId == row.Local.Id)
                    ?? local.FirstOrDefault(t => t.Source.Trim() == row.Local.Source && t.Target.Trim() == row.Local.Target);
                if (previous != null) metadata[(selected.Source.Trim(), selected.Target.Trim())] = previous;
            }
            var saved = await cloud.SaveCloudGlossaryAsync(latest, merged);
            if (context != CloudGlossaryContext) return;
            _cloudGlossaryBasis = saved;
            if (!Active()) { TermFeedback = "合并内容已保存到云端；当前草稿发生变化，未替换本机内容。"; return; }
            TermDraft.Clear();
            foreach (var entry in saved.Entries)
            {
                var term = metadata.TryGetValue((entry.Source.Trim(), entry.Target.Trim()), out var previous)
                    ? previous.Clone() : new GlossaryEntry { SourceKind = GlossarySource.User };
                term.CloudId = entry.Id; term.CloudNote = entry.Note; term.Source = entry.Source; term.Target = entry.Target;
                term.Category = entry.Category; term.Folder = entry.Folder; term.Enabled = entry.Enabled;
                TermDraft.Add(term);
            }
            SelectedTerm = null; RefreshTermView();
            IsCloudGlossarySyncing = false;
            TermFeedback = SaveTermEditor() ? "合并已保存到云端，并更新本机最新词库。" : "云端已保存，合并结果仍在草稿；本机保存未完成，请检查并重试保存。";
        }
        catch (CloudGlossaryException ex) { if (context == CloudGlossaryContext) TermFeedback = ex.Message; }
        catch (InvalidOperationException ex) { if (context == CloudGlossaryContext) TermFeedback = ex.Message; }
        catch { if (context == CloudGlossaryContext) TermFeedback = "未能确认云端合并，请检查网络。本机草稿仍保留。"; }
        finally { IsCloudGlossarySyncing = false; }
    }
}
