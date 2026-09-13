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
    public ObservableCollection<GlossaryEntry> TermDraft { get; } = new();
    private string _termBaseline = "[]";
    private bool _termsLoaded;
    [ObservableProperty] private GlossaryEntry? _selectedTerm;
    [ObservableProperty] private string _termSearch = "";
    [ObservableProperty] private string _termFeedback = "编辑术语后保存到本机。不会自动上传。";
    [ObservableProperty] private bool _termConflictsOnly;
    [ObservableProperty] private double _termWidth = 330;
    public ICollectionView TermView => CollectionViewSource.GetDefaultView(TermDraft);
    public bool IsEditingTerm => SelectedTerm != null;
    public string TermCountText => $"共 {TermDraft.Count} 条 · 当前 {TermView.Cast<object>().Count()} 条";
    public bool HasUnsavedTerms => _termsLoaded && JsonSerializer.Serialize(TermDraft) != _termBaseline;
    partial void OnSelectedTermChanged(GlossaryEntry? value) => OnPropertyChanged(nameof(IsEditingTerm));
    partial void OnTermSearchChanged(string value) => RefreshTermView();
    partial void OnTermConflictsOnlyChanged(bool value) => RefreshTermView();
    public void RefreshTermView()
    {
        var conflicts = TermDraft.Where(t => t.Enabled).GroupBy(t => t.Source, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(t => t.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TermView.Filter = o => o is GlossaryEntry t && (!TermConflictsOnly || conflicts.Contains(t.Source)) && (string.IsNullOrWhiteSpace(TermSearch) || string.Join(" ", t.Source, t.Target, t.Category, t.Folder).Contains(TermSearch, StringComparison.OrdinalIgnoreCase));
        TermView.Refresh(); OnPropertyChanged(nameof(TermCountText));
    }
    public void LoadTermEditor()
    {
        if (HasUnsavedTerms) return;
        TermDraft.Clear(); foreach (var e in GlossaryEntries) TermDraft.Add(e.Clone());
        _termsLoaded = true; _termBaseline = JsonSerializer.Serialize(TermDraft); SelectedTerm = null; RefreshTermView();
    }
    [RelayCommand] private void AddTerm() { var entry = new GlossaryEntry { SourceKind = GlossarySource.User }; TermDraft.Add(entry); TermConflictsOnly = false; TermSearch = ""; SelectedTerm = entry; RefreshTermView(); }
    [RelayCommand] private void FinishTermEdit() { RefreshTermView(); SelectedTerm = null; }
    [RelayCommand] private void DeleteTerm()
    {
        if (SelectedTerm == null) return;
        if (Views.PromptDialog.Show("确定删除选中的术语？保存后生效。", "删除术语", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        TermDraft.Remove(SelectedTerm); SelectedTerm = null; RefreshTermView();
    }
    public bool SaveTermEditor()
    {
        if (IsGlossaryLoading) { TermFeedback = "正在加载术语，请稍候再保存。"; return false; }
        if (IsProcessing) { TermFeedback = "任务执行中，请完成或停止任务后再保存术语。"; return false; }
        if (TermDraft.Count > 1000 || TermDraft.Any(t => string.IsNullOrWhiteSpace(t.Source) || string.IsNullOrWhiteSpace(t.Target))) { TermFeedback = "原文和译文不能为空，术语不能超过 1000 条。"; return false; }
        try
        {
            var entries = TermDraft.Select(t => { var c = t.Clone(); c.Source = c.Source.Trim(); c.Target = c.Target.Trim(); return c; }).ToList();
            var conflicts = entries.Where(t => t.Enabled).GroupBy(t => (t.Source.ToUpperInvariant(), t.PriorityWeight)).Any(g => g.Select(t => t.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            if (conflicts) { TermFeedback = "存在同优先级的不同译法。请筛选冲突，修改译文或停用其中一条后保存。"; TermConflictsOnly = true; return false; }
            var path = Path.Combine(App.AppDataDir, "glossaries", TranslationLanguages.GlossaryFileName(CurrentSourceLang, CurrentTargetLang));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(tmp, JsonSerializer.Serialize(entries, AppConfigJson.WriteOptions)); File.Move(tmp, path, true); }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            _glossaryService.LoadGlossaryAsync(path).GetAwaiter().GetResult();
            RefreshGlossaryDataFromList(entries); RefreshGlossaryConflicts();
            _termBaseline = JsonSerializer.Serialize(TermDraft); TermFeedback = "术语已保存到本机。"; RefreshTermView(); return true;
        }
        catch { TermFeedback = "保存失败，请检查术语目录权限。未保存的编辑仍保留。"; return false; }
    }
    [RelayCommand] private void SaveTerms() => SaveTermEditor();
    [RelayCommand] private void DiscardTerms() { _termsLoaded = false; LoadTermEditor(); TermFeedback = "已放弃未保存的术语修改。"; }
    public bool ConfirmLeaveGlossary()
    {
        if (!HasUnsavedTerms) return true;
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
        if (!RequireAccount()) return;
        if (HasUnsavedTerms && !SaveTermEditor()) return;
        if (Views.PromptDialog.Show("上传会覆盖账号的云端术语，确定继续？", "上传术语", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { var entries = TermDraft.Select(t => new CloudGlossaryEntry { Source=t.Source, Target=t.Target, Category=t.Category, Folder=t.Folder, Enabled=t.Enabled }).ToList(); TermFeedback = await _apiClient.PutGlossaryAsync(entries) ? "已上传术语。" : "上传失败，请稍后重试。"; }
        catch { TermFeedback = "无法上传，请检查登录状态与网络。"; }
    }
    [RelayCommand] private async Task DownloadTermsAsync()
    {
        if (!RequireAccount() || !ConfirmLeaveGlossary()) return;
        if (Views.PromptDialog.Show("下载会替换当前术语草稿，保存到本机后生效。继续？", "下载术语", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            var entries = await _apiClient.GetGlossaryAsync();
            if (entries == null || entries.Count > 1000) { TermFeedback = "云端数据无效或超过 1000 条，未导入。"; return; }
            TermDraft.Clear(); foreach (var t in entries) TermDraft.Add(new GlossaryEntry { Source=t.Source,Target=t.Target,Category=t.Category,Folder=t.Folder,Enabled=t.Enabled,SourceKind=GlossarySource.User });
            SelectedTerm=null; RefreshTermView(); TermFeedback="已下载到草稿，请检查并保存。";
        }
        catch { TermFeedback = "无法下载，请检查登录状态与网络。"; }
    }
}
