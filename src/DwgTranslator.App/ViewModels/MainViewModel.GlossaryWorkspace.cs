using CommunityToolkit.Mvvm.ComponentModel;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Windows.Threading;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _isTermDrawerOpen;
    [ObservableProperty] private bool _isWorkspaceSaving;
    [ObservableProperty] private string _termCategoryFilter = "";
    [ObservableProperty] private int _termSourceFilter;
    private List<GlossaryEntry>? _drawerSnapshot;
    private DispatcherTimer? _searchTimer;
    public string TermLocalStatus => IsWorkspaceSaving ? "本机：保存中…" : HasUnsavedTerms ? "本机：草稿未保存" : _termsLoaded ? "本机：已保存" : "本机：尚未加载";
    public string TermCloudStatus => IsCloudGlossarySyncing ? "云端：同步中…" : _cloudGlossaryContext == CloudGlossaryContext && _cloudGlossaryBasis != null ? "云端：已核对版本 · 手动同步" : "云端：未核对版本 · 手动同步";
    partial void OnIsCloudGlossarySyncingChanged(bool value) => OnPropertyChanged(nameof(TermCloudStatus));
    public bool ShowTermDraftActions => !IsWorkspaceSaving && !IsTermDrawerOpen && HasUnsavedTerms;
    public bool CanEditWorkspace => !IsWorkspaceSaving && !IsProcessing && !IsGlossaryLoading && !IsCloudGlossarySyncing;
    public bool CanEditSelectedContent => SelectedTerm?.SourceKind == GlossarySource.User;
    public string AllTermsLabel => $"全部 {TermDraft.Count}";
    public string EnabledTermsLabel => $"已启用 {TermDraft.Count(t => t.Enabled)}";
    public string DisabledTermsLabel => $"已停用 {TermDraft.Count(t => !t.Enabled)}";
    public string ConflictTermsLabel { get { var conflicts = ConflictSources(); return $"冲突 {TermDraft.Count(t => conflicts.Contains(t.LocalId))}"; } }
    public IEnumerable<string> TermCategories => new[] { "默认分类" }.Concat(_workspace.Categories).Concat(TermDraft.Select(t => string.IsNullOrWhiteSpace(t.Category) ? "默认分类" : t.Category)).Distinct(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> TermCategoryFilters => new[] { "" }.Concat(TermCategories);
    public IReadOnlyList<GlossaryEntry> SelectedTermConflicts => SelectedTerm == null ? Array.Empty<GlossaryEntry>() : EffectiveGlossary.Conflicts(TermDraft).Where(t => t != SelectedTerm && t.SourceLang == SelectedTerm.SourceLang && t.TargetLang == SelectedTerm.TargetLang && string.Equals(t.Source.Trim(), SelectedTerm.Source.Trim(), StringComparison.OrdinalIgnoreCase) && !string.Equals(t.Target.Trim(), SelectedTerm.Target.Trim(), StringComparison.Ordinal)).ToList();
    partial void OnIsTermDrawerOpenChanged(bool value) { OnPropertyChanged(nameof(ShowTermDraftActions)); OnPropertyChanged(nameof(IsEditingTerm)); OnPropertyChanged(nameof(CanEditSelectedContent)); OnPropertyChanged(nameof(SelectedTermConflicts)); }
    partial void OnTermCategoryFilterChanged(string value) => RefreshTermView();
    partial void OnTermSourceFilterChanged(int value) => RefreshTermView();
    private bool MatchesWorkspaceFilters(GlossaryEntry t) => (string.IsNullOrEmpty(TermCategoryFilter) || t.Category == TermCategoryFilter) && (TermSourceFilter == 0 || TermSourceFilter == 1 && t.SourceKind == GlossarySource.System || TermSourceFilter == 2 && t.SourceKind == GlossarySource.Enterprise || TermSourceFilter == 3 && t.SourceKind == GlossarySource.User || TermSourceFilter == 4 && t.CloudId != null);
    public bool HasTermConflict(GlossaryEntry term) => ConflictSources().Contains(term.LocalId);
    private HashSet<string> ConflictSources() => DwgTranslator.Core.Services.EffectiveGlossary.Conflicts(TermDraft).Select(t => t.LocalId).ToHashSet();
    private void RefreshWorkspaceCounts()
    {
        foreach (var name in new[] { nameof(AllTermsLabel), nameof(EnabledTermsLabel), nameof(DisabledTermsLabel), nameof(ConflictTermsLabel), nameof(TermCategories), nameof(TermCategoryFilters), nameof(SelectedTermConflicts) }) OnPropertyChanged(name);
    }
    private void QueueTermSearch()
    {
        if (_searchTimer == null) { _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) }; _searchTimer.Tick += OnTermSearchTimerTick; }
        _searchTimer.Stop(); _searchTimer.Start();
    }
    private void OnTermSearchTimerTick(object? sender, EventArgs e) { _searchTimer?.Stop(); RefreshTermView(); }
    /// <summary>Releases the debounce timer so a closed workspace stops being kept alive by its Tick.</summary>
    private void StopTermSearchTimer()
    {
        if (_searchTimer == null) return;
        _searchTimer.Stop(); _searchTimer.Tick -= OnTermSearchTimerTick; _searchTimer = null;
    }
    private void BeginDrawerSnapshot() => _drawerSnapshot = TermDraft.Select(t => t.Clone()).ToList();
    public void OpenTermDrawer(GlossaryEntry term)
    {
        if (!CanEditWorkspace || IsTermDrawerOpen) return;
        BeginDrawerSnapshot(); SelectedTerm = term; IsTermDrawerOpen = true; TermFeedback = "正在查看 / 编辑术语；修改需保存到本机，云端仍需手动同步。";
    }
    public void CopyTermToDrawer(GlossaryEntry term)
    {
        if (!CanEditWorkspace || IsTermDrawerOpen) return;
        BeginDrawerSnapshot(); var copy = term.Clone(); copy.LocalId = Guid.NewGuid().ToString("D"); copy.CloudId = null; copy.SourceKind = GlossarySource.User; copy.Enabled = false;
        TermDraft.Add(copy); SelectedTerm = copy; IsTermDrawerOpen = true; TermFeedback = "已复制为用户术语草稿，请检查并保存到本机。"; RefreshTermView();
    }
    public void CancelTermDrawer()
    {
        if (_drawerSnapshot != null) RestoreWorkspace(_drawerSnapshot);
        CloseTermDrawer();
        TermFeedback = HasUnsavedTerms ? "已取消本次编辑，原有未保存草稿仍保留。" : "已取消本次编辑，本机内容未改变。";
    }
    private void CloseTermDrawer() { _drawerSnapshot = null; IsTermDrawerOpen = false; SelectedTerm = null; RefreshTermView(); }
    private void RestoreWorkspace(IEnumerable<GlossaryEntry> snapshot)
    {
        TermDraft.Clear(); foreach (var term in snapshot) TermDraft.Add(term.Clone()); RefreshTermView();
    }
    // One atomic local-file transaction: a batch either persists in full or rolls back in full.
    public bool CommitWorkspaceChange(Action change)
    {
        if (!CanEditWorkspace) { TermFeedback = "正在执行任务或同步，请稍候再编辑术语。"; return false; }
        var before = TermDraft.Select(t => t.Clone()).ToList();
        change();
        if (SaveTermEditor()) return true;
        var error = TermFeedback; RestoreWorkspace(before); TermFeedback = error + " 状态已恢复，未提交本次更改。"; return false;
    }

    public async Task<bool> CommitWorkspaceChangeAsync(Action change)
    {
        if (!CanEditWorkspace || IsTermDrawerOpen) { TermFeedback = "请先完成当前编辑或等待保存完成。"; return false; }
        var before = TermDraft.Select(t => t.Clone()).ToList();
        var context = CloudGlossaryContext;
        var path = WorkspacePath;
        change();
        var entries = TermDraft.Select(t => { var copy = t.Clone(); copy.Source = copy.Source.Trim(); copy.Target = copy.Target.Trim(); return copy; }).ToList();
        if (entries.Any(t => !DwgTranslator.Core.Services.EffectiveGlossary.Valid(t)))
        {
            RestoreWorkspace(before); TermFeedback = "保存失败：字段为空或超长。状态已恢复。"; Services.ToastService.Error(TermFeedback); return false;
        }
        IsWorkspaceSaving = true; IsGlossaryLoading = true; TermFeedback = "正在保存到本机 · 云端需手动同步"; RefreshTermView();
        await _glossaryLoadGate.WaitAsync();
        var committed = false;
        try
        {
            if (context != CloudGlossaryContext) return false;
            var document = new DwgTranslator.Core.Infrastructure.Glossary.GlossaryWorkspaceDocument {
                Entries=entries.Select(t=>t.Clone()).ToList(), Categories=_workspace.Categories.ToList(),
                SyncBasis=new(_workspace.SyncBasis), PendingUploads=new(_workspace.PendingUploads), LastCheckedAt=_workspace.LastCheckedAt
            };
            await Task.Run(()=>DwgTranslator.Core.Infrastructure.Glossary.GlossaryWorkspaceStore.Save(path, document));
            committed = true;
            if (context != CloudGlossaryContext) return true; // Never project an old account's completion into the new session.
            _workspace = document;
            await _glossaryService.LoadGlossaryAsync(path);
            if (context != CloudGlossaryContext) return true;
            RefreshGlossaryDataFromList(entries); RefreshGlossaryConflicts();
            _termBaseline = System.Text.Json.JsonSerializer.Serialize(TermDraft);
            TermFeedback = "本机已保存 · 云端需手动同步"; RefreshTermView(); return true;
        }
        catch (Exception ex)
        {
            if (context == CloudGlossaryContext)
            {
                if (!committed) RestoreWorkspace(before);
                else _termBaseline = System.Text.Json.JsonSerializer.Serialize(TermDraft);
                TermFeedback = committed ? "本机文件已保存，但词库重新加载失败，请重试加载后再翻译。" : "保存失败，所有更改已恢复：" + ex.Message;
            }
            if (context == CloudGlossaryContext) Services.ToastService.Error(TermFeedback);
            return committed;
        }
        finally { IsWorkspaceSaving = false; IsGlossaryLoading = false; _glossaryLoadGate.Release(); RefreshTermView(); }
    }
    public Task<bool> SetWorkspaceEnabledAsync(IReadOnlyList<GlossaryEntry> terms, bool enabled)
        => CommitWorkspaceChangeAsync(() => { foreach (var t in terms.Where(TermDraft.Contains)) t.Enabled = enabled; });
    public Task<bool> EditWorkspaceTextAsync(GlossaryEntry term, string field, string value)
    {
        if (term.SourceKind != GlossarySource.User || !TermDraft.Contains(term)) return Task.FromResult(false);
        return CommitWorkspaceChangeAsync(() => { switch(field) { case "Source": term.Source=value; break; case "Target": term.Target=value; break; case "Category": term.Category=value; break; } });
    }
    public bool SetWorkspaceEnabled(IReadOnlyList<GlossaryEntry> terms, bool enabled)
        => CommitWorkspaceChange(() => { foreach (var t in terms.Where(TermDraft.Contains)) t.Enabled = enabled; });
    public bool EditWorkspaceText(GlossaryEntry term, string field, string value)
    {
        if (term.SourceKind != GlossarySource.User || !TermDraft.Contains(term)) return false;
        return CommitWorkspaceChange(() => { switch (field) { case "Source": term.Source = value; break; case "Target": term.Target = value; break; case "Category": term.Category = value; break; } });
    }
}
