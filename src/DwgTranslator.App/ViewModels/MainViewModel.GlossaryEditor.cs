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
    [ObservableProperty] private string _termFeedback = "本地编辑即时保存 · 云端需手动同步";
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
        // WPF cannot filter during an active DataGrid edit. Preserve the editor and apply after it ends.
        if (TermView is IEditableCollectionView editing && (editing.IsEditingItem || editing.IsAddingNew)) { QueueTermSearch(); return; }
        var conflicts = TermDraft.Where(t => t.Enabled).GroupBy(t => t.Source, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(t => t.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TermView.Filter = o => o is GlossaryEntry t && MatchesWorkspaceFilters(t) && (TermScope == 0 || TermScope == 1 && t.LastHitAt.HasValue && t.LastHitAt.Value >= DateTime.UtcNow.AddDays(-30) || TermScope == 2 && t.SourceKind == GlossarySource.User) && (!TermConflictsOnly || conflicts.Contains(t.Source)) && (TermStatusFilter == "All" || TermStatusFilter == "Conflict" || (TermStatusFilter == "Enabled" && t.Enabled) || (TermStatusFilter == "Disabled" && !t.Enabled)) && (string.IsNullOrWhiteSpace(TermSearch) || string.Join(" ", t.Source, t.Target, t.Category, t.Folder).Contains(TermSearch, StringComparison.OrdinalIgnoreCase));
        TermView.Refresh(); NotifyTermDraftEdited(); RefreshWorkspaceCounts();
    }
    public void LoadTermEditor()
    {
        if (HasUnsavedTerms) return;
        _drawerSnapshot = null; IsTermDrawerOpen = false; TermDraft.Clear(); foreach (var e in GlossaryEntries) TermDraft.Add(e.Clone());
        _termsLoaded = true; _termBaseline = JsonSerializer.Serialize(TermDraft); SelectedTerm = null; RefreshTermView();
    }
    [RelayCommand] private void AddTerm() { if (IsTermDrawerOpen || !CanEditWorkspace) return; BeginDrawerSnapshot(); var entry = new GlossaryEntry { SourceKind = GlossarySource.User }; TermDraft.Add(entry); TermScope = 0; TermConflictsOnly = false; TermSearch = ""; SelectedTerm = entry; IsTermDrawerOpen = true; TermFeedback = "正在新增用户术语，请填写后保存到本机。"; RefreshTermView(); }
    [RelayCommand] private void FinishTermEdit() { if (SaveTermEditor()) CloseTermDrawer(); }
    [RelayCommand] private void DeleteTerm()
    {
        if (SelectedTerm == null || SelectedTerm.SourceKind != GlossarySource.User) return;
        if (Views.PromptDialog.Show("确定删除选中的术语？保存后生效。", "删除术语", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var term = SelectedTerm; if (CommitWorkspaceChange(() => TermDraft.Remove(term))) CloseTermDrawer();
    }
    public bool SaveTermEditor()
    {
        if (IsCloudGlossarySyncing) { TermFeedback = "正在同步云端，请稍候。"; return false; }
        if (IsGlossaryLoading) { TermFeedback = "正在加载术语，请稍候再保存。"; return false; }
        if (IsProcessing) { TermFeedback = "任务执行中，请完成或停止任务后再保存术语。"; return false; }
        if (TermDraft.Count > 1000 || TermDraft.Any(t => string.IsNullOrWhiteSpace(t.Source) || string.IsNullOrWhiteSpace(t.Target))) { TermFeedback = "原文和译文不能为空，术语不能超过 1000 条。"; return false; }
        try
        {
            var entries = TermDraft.Select(t => { var c = t.Clone(); c.Source = c.Source.Trim(); c.Target = c.Target.Trim(); return c; }).ToList();
            var conflicts = entries.Where(t => t.Enabled).GroupBy(t => (t.Source.ToUpperInvariant(), t.PriorityWeight)).Any(g => g.Select(t => t.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            if (conflicts) { TermFeedback = "存在同优先级的不同译法。请筛选冲突，修改译文或停用其中一条后保存。"; TermConflictsOnly = true; return false; }
            var path = Path.Combine(AccountDataDirectory, "glossaries", TranslationLanguages.GlossaryFileName(CurrentSourceLang, CurrentTargetLang));
            DwgTranslator.Core.Infrastructure.Glossary.GlossaryFileStore.Save(path, entries);
            _glossaryService.LoadGlossaryAsync(path).GetAwaiter().GetResult();
            RefreshGlossaryDataFromList(entries); RefreshGlossaryConflicts();
            _termBaseline = JsonSerializer.Serialize(TermDraft); if (IsTermDrawerOpen) CloseTermDrawer(); TermFeedback = "本机已保存 · 云端需手动同步"; RefreshTermView(); return true;
        }
        catch { TermFeedback = "保存失败，请检查术语目录权限。未保存的编辑仍保留。"; return false; }
    }
    [RelayCommand] private void SaveTerms() => SaveTermEditor();
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
    [RelayCommand] private void ExportTerms()
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "JSON 术语库|*.json", FileName = "glossary.json" };
        if (d.ShowDialog() != true) return;
        try { File.WriteAllText(d.FileName, JsonSerializer.Serialize(TermDraft, AppConfigJson.WriteOptions)); TermFeedback = "已导出当前术语。"; } catch { TermFeedback = "导出失败，请检查文件权限。"; }
    }
    [RelayCommand] private async Task UploadTermsAsync()
    {
        if (IsCloudGlossarySyncing || IsGlossaryLoading || IsProcessing || !RequireAccount()) return;
        if (_apiClient is not ICloudGlossaryClient cloud) { TermFeedback = "当前连接不支持安全云端同步。"; return; }
        if (!SaveTermEditor()) return;
        var context = CloudGlossaryContext;
        var draft = JsonSerializer.Serialize(TermDraft);
        IsCloudGlossarySyncing = true;
        TermFeedback = "正在核对云端版本……";
        try
        {
            if (_cloudGlossaryContext != context) { _cloudGlossaryBasis = null; _cloudGlossaryContext = context; }
            if (_cloudGlossaryBasis == null)
            {
                _cloudGlossaryBasis = await cloud.ReadCloudGlossaryAsync();
                if (context != CloudGlossaryContext) return;
                if (_cloudGlossaryBasis.Entries.Count > 1000) { TermFeedback = "云端超过1000条，已停止整库覆盖，请先在网页整理。"; return; }
            }
            if (context != CloudGlossaryContext || draft != JsonSerializer.Serialize(TermDraft)) return;
            if (Views.PromptDialog.Show($"将以本机 {TermDraft.Count} 条术语替换当前账号云端的 {_cloudGlossaryBasis.Entries.Count} 条术语。云端词库由所有语言方向共用，不会自动合并。\n如需保留云端词条，请取消并先下载核对。确定上传？", "确认云端保存", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            { TermFeedback = "已取消上传，本机内容不变。"; return; }
            var entries = TermDraft.Select(t => new CloudGlossaryEntry { Id=t.CloudId, Note=t.CloudNote, Source=t.Source.Trim(), Target=t.Target.Trim(), Category=t.Category, Folder=t.Folder, Enabled=t.Enabled }).ToList();
            var saved = await cloud.SaveCloudGlossaryAsync(_cloudGlossaryBasis, entries);
            if (context != CloudGlossaryContext) return;
            _cloudGlossaryBasis = saved;
            if (draft != JsonSerializer.Serialize(TermDraft)) { TermFeedback = "提交内容已保存到云端；当前编辑有变化，仍需另行保存。"; return; }
            // Preserve returned IDs/notes even after renaming source text on this device.
            for (var i = 0; i < TermDraft.Count; i++)
            { TermDraft[i].CloudId = saved.Entries[i].Id; TermDraft[i].CloudNote = saved.Entries[i].Note; }
            IsCloudGlossarySyncing = false;
            TermFeedback = SaveTermEditor() ? "已保存到云端，并更新本机最新词库。" : "云端已保存，但本机保存失败；请重试本机保存。";
        }
        catch (CloudGlossaryException ex) { if (context == CloudGlossaryContext) TermFeedback = ex.Message; }
        catch { if (context == CloudGlossaryContext) TermFeedback = "未能确认云端保存，请检查登录状态与网络。本机内容仍保留。"; }
        finally { IsCloudGlossarySyncing = false; }
    }
    [RelayCommand] private async Task DownloadTermsAsync()
    {
        if (IsCloudGlossarySyncing || IsGlossaryLoading || IsProcessing || !RequireAccount() || !ConfirmLeaveGlossary()) return;
        if (_apiClient is not ICloudGlossaryClient cloud) { TermFeedback = "当前连接不支持安全云端同步。"; return; }
        if (Views.PromptDialog.Show("下载会替换当前术语草稿，保存到本机后生效。请先导出需要保留的本机词条。继续？", "下载最新云端术语", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var context = CloudGlossaryContext;
        var draft = JsonSerializer.Serialize(TermDraft);
        IsCloudGlossarySyncing = true;
        TermFeedback = "正在读取最新云端词库……";
        try
        {
            var latest = await cloud.ReadCloudGlossaryAsync();
            if (context != CloudGlossaryContext || draft != JsonSerializer.Serialize(TermDraft)) return;
            if (latest.Entries.Count > 1000) { TermFeedback = "云端超过1000条，未截断或替换本机内容。"; return; }
            _cloudGlossaryBasis = latest; _cloudGlossaryContext = context;
            TermDraft.Clear();
            foreach (var t in latest.Entries) TermDraft.Add(new GlossaryEntry { CloudId=t.Id, CloudNote=t.Note, Source=t.Source, Target=t.Target, Category=t.Category, Folder=t.Folder, Enabled=t.Enabled, SourceKind=GlossarySource.User });
            SelectedTerm = null; RefreshTermView(); TermFeedback = "已读取最新云端词库到草稿，请检查并保存到本机。";
        }
        catch (CloudGlossaryException ex) { if (context == CloudGlossaryContext) TermFeedback = ex.Message; }
        catch { if (context == CloudGlossaryContext) TermFeedback = "无法读取云端，请检查登录状态与网络。本机内容未替换。"; }
        finally { IsCloudGlossarySyncing = false; }
    }
}
