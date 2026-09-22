using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows;
using System.IO;
using System.Text.Json;
using Serilog;
namespace DwgTranslator.App.ViewModels;
public partial class MainViewModel
{
    [ObservableProperty] private bool _isGlossaryLoading;
    [ObservableProperty] private bool _isCloudGlossarySyncing;
    private CloudGlossaryState? _cloudGlossaryBasis;
    private string? _cloudGlossaryContext;
    private string CloudGlossaryContext => $"{_sessionVersion}|{_config.ActiveAccountId}|{CurrentSourceLang}|{CurrentTargetLang}";
    public ObservableCollection<GlossaryEntry> TermDraft { get; } = new();
    private string _termBaseline = "[]";
    private bool _termsLoaded;
    [ObservableProperty] private GlossaryEntry? _selectedTerm;
    [ObservableProperty] private string _termSearch = "";
    [ObservableProperty] private string _termFeedback = "";
    [ObservableProperty] private bool _termConflictsOnly;
    [ObservableProperty] private string _termStatusFilter = "All";
    [ObservableProperty] private int _termScope;
    partial void OnTermScopeChanged(int value) => RefreshTermView();
    [ObservableProperty] private double _termWidth = 330;
    public ICollectionView TermView => CollectionViewSource.GetDefaultView(TermDraft);
    public bool IsEditingTerm => IsTermDrawerOpen;
    public string TermCountText => $"{TermView.Cast<object>().Count()} / {TermDraft.Count} 条";
    public bool HasUnsavedTerms => _termsLoaded && JsonSerializer.Serialize(TermDraft) != _termBaseline;
    partial void OnSelectedTermChanged(GlossaryEntry? value) => OnPropertyChanged(nameof(IsEditingTerm));
    partial void OnTermSearchChanged(string value) => QueueTermSearch();
    partial void OnTermConflictsOnlyChanged(bool value) { if (value) TermStatusFilter = "Conflict"; else if (TermStatusFilter == "Conflict") TermStatusFilter = "All"; RefreshTermView(); }
    partial void OnTermStatusFilterChanged(string value) { TermConflictsOnly = value == "Conflict"; RefreshTermView(); }
    public void NotifyTermDraftEdited()
    {
        OnPropertyChanged(nameof(HasUnsavedTerms));
        OnPropertyChanged(nameof(ShowTermDraftActions));
        OnPropertyChanged(nameof(TermLocalStatus));
        OnPropertyChanged(nameof(TermCloudStatus));
        OnPropertyChanged(nameof(TermCountText));
    }
    public void RefreshTermView()
    {
        RefreshSyncStatuses();
        // WPF cannot filter during an active DataGrid edit. Preserve the editor and apply after it ends.
        if (TermView is IEditableCollectionView editing && (editing.IsEditingItem || editing.IsAddingNew)) { QueueTermSearch(); return; }
        var conflicts = DwgTranslator.Core.Services.EffectiveGlossary.Conflicts(TermDraft).Select(t => t.LocalId).ToHashSet();
        TermView.Filter = o => o is GlossaryEntry t && MatchesWorkspaceFilters(t) && (TermScope == 0 || TermScope == 1 && t.LastHitAt.HasValue && t.LastHitAt.Value >= DateTime.UtcNow.AddDays(-30) || TermScope == 2 && t.SourceKind == GlossarySource.User) && (!TermConflictsOnly || conflicts.Contains(t.LocalId)) && (TermStatusFilter == "All" || TermStatusFilter == "Conflict" || (TermStatusFilter == "Enabled" && t.Enabled) || (TermStatusFilter == "Disabled" && !t.Enabled)) && (string.IsNullOrWhiteSpace(TermSearch) || string.Join(" ", t.Source, t.Target, t.Category, t.Folder).Contains(TermSearch, StringComparison.OrdinalIgnoreCase));
        TermView.Refresh(); NotifyTermDraftEdited(); RefreshWorkspaceCounts();
    }
    public void LoadTermEditor()
    {
        if (HasUnsavedTerms) return;
        _drawerSnapshot = null; IsTermDrawerOpen = false; TermDraft.Clear(); foreach (var e in GlossaryEntries) TermDraft.Add(e.Clone());
        _termsLoaded = true; _termBaseline = JsonSerializer.Serialize(TermDraft); SelectedTerm = null; RefreshTermView();
    }
    [RelayCommand]
    private void AddTerm()
    {
        if (IsTermDrawerOpen)
        {
            TermFeedback = "请先完成当前术语编辑。";
            DwgTranslator.App.Services.ToastService.Info(TermFeedback);
            return;
        }
        if (!CanEditWorkspace)
        {
            TermFeedback = "正在执行任务、保存或同步，请稍候再添加术语。";
            DwgTranslator.App.Services.ToastService.Info(TermFeedback);
            return;
        }
        BeginDrawerSnapshot();
        var entry = new GlossaryEntry { SourceKind = GlossarySource.User, SourceLang = CurrentSourceLang, TargetLang = CurrentTargetLang };
        TermDraft.Add(entry); TermScope = 0; TermConflictsOnly = false; TermSearch = ""; SelectedTerm = entry; IsTermDrawerOpen = true;
        TermFeedback = "正在新增用户术语，请填写后保存到本机。";
        RefreshTermView();
    }
    [RelayCommand] private async Task FinishTermEditAsync() { if (await SaveTermEditorAsync()) CloseTermDrawer(); }
    [RelayCommand] private void DeleteTerm()
    {
        if (SelectedTerm == null || SelectedTerm.SourceKind != GlossarySource.User) return;
        if (Views.PromptDialog.Show("仅删除本机术语，云端副本保留。确定删除？", "删除术语", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var term = SelectedTerm; if (CommitWorkspaceChange(() => TermDraft.Remove(term))) CloseTermDrawer();
    }
    /// <summary>Guards + validation shared by the synchronous and asynchronous save paths.</summary>
    private bool TryPrepareTermSave(out List<GlossaryEntry> entries, out string path)
    {
        entries = new List<GlossaryEntry>();
        path = string.Empty;
        if (IsCloudGlossarySyncing) { TermFeedback = "正在同步云端，请稍候。"; return false; }
        if (IsGlossaryLoading) { TermFeedback = "正在加载术语，请稍候再保存。"; return false; }
        if (IsProcessing) { TermFeedback = "任务执行中，请完成或停止任务后再保存术语。"; return false; }
        if (TermDraft.Any(t => !EffectiveGlossary.Valid(t))) { TermFeedback = "原文/译文需1–500字，分类/目录最多128字，备注最多1000字。"; return false; }
        entries = TermDraft.Select(t => { var c = t.Clone(); c.Source = c.Source.Trim(); c.Target = c.Target.Trim(); return c; }).ToList();
        path = WorkspacePath;
        foreach (var e in TermDraft) EffectiveGlossary.Normalize(e);
        return true;
    }

    private bool CompleteTermSave(List<GlossaryEntry> entries)
    {
        RefreshGlossaryDataFromList(entries); RefreshGlossaryConflicts();
        _termBaseline = JsonSerializer.Serialize(TermDraft); if (IsTermDrawerOpen) CloseTermDrawer(); TermFeedback = "已写入本机术语库"; RefreshTermView(); return true;
    }

    /// <summary>Failure path for both save flavours: keeps the message and the log in one place.</summary>
    private bool FailTermSave(bool committed, Exception ex)
    {
        Log.Warning(ex, "保存本机术语失败");
        if (committed) _termBaseline = JsonSerializer.Serialize(TermDraft);
        TermFeedback = committed ? "本机文件已保存，但词库加载失败，请重新加载后再翻译。" : "保存失败，请检查术语目录权限。未保存的编辑仍保留。";
        return committed;
    }

    public async Task<bool> SaveTermEditorAsync()
    {
        if (!TryPrepareTermSave(out var entries, out var path)) return false;
        var committed = false;
        try
        {
            PersistWorkspace(entries);
            committed = true;
            // The glossary service must see the new file before anyone translates with these terms.
            await _glossaryService.LoadGlossaryAsync(path);
            return CompleteTermSave(entries);
        }
        catch (Exception ex) { return FailTermSave(committed, ex); }
    }

    /// <summary>
    /// Synchronous twin, kept only for callers that structurally cannot await — the window-closing
    /// prompt and the atomic <see cref="CommitWorkspaceChange"/> helpers. The glossary reload runs on
    /// a worker thread (Task.Run) so the UI thread only blocks on its completion, not on the file IO
    /// itself; every caller that can await must use <see cref="SaveTermEditorAsync"/>.
    /// </summary>
    public bool SaveTermEditor()
    {
        if (!TryPrepareTermSave(out var entries, out var path)) return false;
        var committed = false;
        try
        {
            PersistWorkspace(entries);
            committed = true;
            // The glossary service must see the new file before anyone translates with these terms.
            // Reload happens on a worker thread to avoid a UI-thread deadlock / long freeze on slow disks.
            var reload = Task.Run(() => _glossaryService.LoadGlossaryAsync(path));
            // 这条路径结构上无法 await（关闭窗口提示等），但仍不能让 UI 无限期冻结：
            // 只等待完成句柄，失败时的异常仍由下面的统一处理给出原始信息。
            if (!((IAsyncResult)reload).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("术语表重新加载超时，请重试保存。");
            reload.GetAwaiter().GetResult();
            return CompleteTermSave(entries);
        }
        catch (Exception ex) { return FailTermSave(committed, ex); }
    }
    [RelayCommand] private Task SaveTermsAsync() => SaveTermEditorAsync();
    [RelayCommand] private void DiscardTerms() { _termsLoaded = false; LoadTermEditor(); TermFeedback = "已放弃未保存的术语修改。"; }
    public bool ConfirmLeaveGlossary()
    {
        if (IsWorkspaceSaving) { TermFeedback = "正在保存本机术语，请稍候再离开。"; return false; }
        if (IsCloudGlossarySyncing) { TermFeedback = "正在同步云端，请稍候再离开或切换语言。"; return false; }
        if (!HasUnsavedTerms) { if (IsTermDrawerOpen) CloseTermDrawer(); return true; }
        var answer = Views.PromptDialog.Show("术语尚未保存。是否保存后继续？", "未保存的术语", MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveTermEditor();
        DiscardTerms(); return true;
    }

    /// <summary>异步版离开确认：能 await 的调用方（账号/云端/导入流程）用它，保存走 SaveTermEditorAsync。</summary>
    public async Task<bool> ConfirmLeaveGlossaryAsync()
    {
        if (IsWorkspaceSaving) { TermFeedback = "正在保存本机术语，请稍候再离开。"; return false; }
        if (IsCloudGlossarySyncing) { TermFeedback = "正在同步云端，请稍候再离开或切换语言。"; return false; }
        if (!HasUnsavedTerms) { if (IsTermDrawerOpen) CloseTermDrawer(); return true; }
        var answer = Views.PromptDialog.Show("术语尚未保存。是否保存后继续？", "未保存的术语", MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return await SaveTermEditorAsync();
        DiscardTerms(); return true;
    }
    [RelayCommand] private void ExportTerms()
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "JSON 术语库|*.json", FileName = "glossary.json" };
        if (d.ShowDialog() != true) return;
        try { File.WriteAllText(d.FileName, JsonSerializer.Serialize(TermDraft.Select(t => new { t.Source, t.Target, t.Category, t.SourceLang, t.TargetLang, t.Folder, t.CloudNote, t.Enabled }), AppConfigJson.WriteOptions)); TermFeedback = "已导出当前术语。"; } catch { TermFeedback = "导出失败，请检查文件权限。"; }
    }
    [RelayCommand] private Task UploadTermsAsync() => UploadSelectedTermsAsync(TermDraft.ToList());
    [RelayCommand] private async Task DownloadTermsAsync() => await OpenCloudWorkspaceAsync();
}
