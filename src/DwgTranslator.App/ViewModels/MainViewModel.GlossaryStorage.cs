using System.IO;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Infrastructure.Glossary;
namespace DwgTranslator.App.ViewModels;
public partial class MainViewModel
{
    private string _workspaceOwner = "";
    private GlossaryWorkspaceDocument _workspace = new();
    private string WorkspacePath => Path.Combine(AccountDataDirectory,"glossaries","workspace-v2.json");
    public IReadOnlyList<TranslationLanguage> TermLanguages => TranslationLanguages.All;
    private void EnsureWorkspace()
    {
        if (_workspaceOwner == WorkspacePath) return;
        _workspace = GlossaryWorkspaceStore.Migrate(WorkspacePath,Path.GetDirectoryName(WorkspacePath)!,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"assets","default-glossaries"));
        _workspaceOwner = WorkspacePath;
    }
    private void PersistWorkspace(IReadOnlyList<GlossaryEntry> entries)
    {
        EnsureWorkspace();
        var doc = new GlossaryWorkspaceDocument { Entries = entries.Select(e => e.Clone()).ToList(), Categories = _workspace.Categories.ToList(), SyncBasis = new(_workspace.SyncBasis), PendingUploads = new(_workspace.PendingUploads), LastCheckedAt = _workspace.LastCheckedAt };
        GlossaryWorkspaceStore.Save(WorkspacePath, doc); _workspace = doc;
    }
    public bool ChangeTermCategory(string? oldName, string? newName, bool delete = false)
    {
        if (!CanEditWorkspace || IsTermDrawerOpen) return false;
        EnsureWorkspace(); var name = newName?.Trim() ?? "";
        if (oldName == EffectiveGlossary.DefaultCategory || (!delete && (name.Length == 0 || name.Length > 128 || name == "全部分类" || _workspace.Categories.Any(c => c.Equals(name,StringComparison.OrdinalIgnoreCase) && c != oldName)))) { TermFeedback = "分类名称为空、重复、保留名称或超过128字；默认分类不可修改。"; return false; }
        var categories = _workspace.Categories.ToList();
        if (oldName != null) _workspace.Categories.Remove(oldName);
        if (!delete) _workspace.Categories.Add(name);
        var ok = CommitWorkspaceChange(() => { foreach(var t in TermDraft.Where(t => t.SourceKind == GlossarySource.User && t.Category == oldName)) t.Category = delete ? EffectiveGlossary.DefaultCategory : name; });
        if (!ok) _workspace.Categories = categories;
        RefreshWorkspaceCounts(); return ok;
    }
    private void RefreshSyncStatuses()
    {
        EnsureWorkspace();
        foreach (var t in TermDraft)
        {
            if (t.SourceKind != GlossarySource.User) { t.SyncStatus = "只读来源"; continue; }
            if (_workspace.PendingUploads.ContainsKey(t.LocalId)) { t.SyncStatus = "待核对"; continue; }
            if (t.CloudId == null) { t.SyncStatus = "仅本机"; continue; }
            t.SyncStatus = _workspace.SyncBasis.TryGetValue(t.CloudId,out var basis) ? CloudGlossaryMerge.Equal(ToCloudTerm(t),basis) ? "已核对一致" : "待上传" : "待核对";
            if (_cloudGlossaryContext == CloudGlossaryContext && _cloudGlossaryBasis != null)
            {
                var remote = _cloudGlossaryBasis.Entries.FirstOrDefault(e => e.Id == t.CloudId);
                if (remote == null) t.SyncStatus = "云端已删除 · 待核对";
                else if (!CloudGlossaryMerge.Equal(remote, basis)) t.SyncStatus = "云端已变化";
            }
        }
    }
    private void ReconcilePendingUploads(CloudGlossaryState latest)
    {
        foreach (var pending in _workspace.PendingUploads.ToList())
        {
            var remote=latest.Entries.SingleOrDefault(e=>e.Id==pending.Value.Id);
            if (remote == null || !CloudGlossaryMerge.Equal(remote,pending.Value)) continue;
            var term=TermDraft.SingleOrDefault(t=>t.LocalId==pending.Key && t.SourceKind==GlossarySource.User);
            if (term != null) { term.CloudId=remote.Id; _workspace.SyncBasis[remote.Id!]=remote; }
            _workspace.PendingUploads.Remove(pending.Key);
        }
    }
    private static CloudGlossaryEntry ToCloudTerm(GlossaryEntry t) => new() { Id=t.CloudId ?? t.LocalId, Source=t.Source.Trim(), Target=t.Target.Trim(), Category=t.Category, Folder=t.Folder, Note=t.CloudNote, Enabled=t.Enabled, SourceLang=t.DirectionPending ? "" : t.SourceLang, TargetLang=t.DirectionPending ? "" : t.TargetLang, DirectionPending=t.DirectionPending };
    private static void ApplyCloudTerm(GlossaryEntry t, CloudGlossaryEntry e)
    {
        t.CloudId=e.Id; t.Source=e.Source; t.Target=e.Target; t.Category=string.IsNullOrWhiteSpace(e.Category) ? EffectiveGlossary.DefaultCategory : e.Category;
        t.Folder=e.Folder; t.CloudNote=e.Note; t.Enabled=e.Enabled; t.SourceLang=e.DirectionPending ? "" : e.SourceLang; t.TargetLang=e.DirectionPending ? "" : e.TargetLang;
    }
}
