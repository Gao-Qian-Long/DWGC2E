using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;
using Serilog;
using System.Collections.ObjectModel;
using System.IO;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    private TranslationProjectStore ProjectStore => new(AccountDataDirectory);
    private CancellationTokenSource? _projectAutosaveCts;

    private async void ScheduleProjectAutosave()
    {
        // Release the previous debounce before replacing it: it was cancelled while a Task.Delay was
        // pending and would otherwise never be disposed for the lifetime of the session.
        _projectAutosaveCts?.Cancel();
        _projectAutosaveCts?.Dispose();
        var cts = _projectAutosaveCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(1200, cts.Token);
            if (!cts.IsCancellationRequested && ActiveTranslationProject != null && !IsProcessing && !IsExporting)
                SaveActiveProject();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "翻译项目自动保存失败"); }
    }

    public ObservableCollection<TranslationProjectSummary> TranslationProjects { get; } = new();

    [ObservableProperty] private TranslationProject? _activeTranslationProject;
    [ObservableProperty] private string _projectSearch = string.Empty;
    [ObservableProperty] private TranslationProjectSummary? _selectedTranslationProject;
    [ObservableProperty] private string _projectRenameName = string.Empty;
    public bool HasActiveTranslationProject => ActiveTranslationProject != null;

    partial void OnActiveTranslationProjectChanged(TranslationProject? value)
    {
        OnPropertyChanged(nameof(HasActiveTranslationProject));
        if (value != null) ProjectRenameName = value.Name;
    }

    partial void OnSelectedTranslationProjectChanged(TranslationProjectSummary? value)
    {
        if (value != null) ProjectRenameName = value.Name;
    }
    partial void OnProjectSearchChanged(string value) => RefreshTranslationProjects();

    private void RefreshTranslationProjects()
    {
        TranslationProjects.Clear();
        foreach (var item in ProjectStore.List(ProjectSearch)) TranslationProjects.Add(item);
    }

    private void ArchiveTranslationRun(IReadOnlyCollection<TranslationTask> tasks)
    {
        var ready = tasks.Where(t => t.Status is TranslationTaskStatus.ReadyForReview or TranslationTaskStatus.Completed)
            .Select(t => NormalizeSourcePath(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entities = Entities.Where(e => ready.Contains(NormalizeSourcePath(e.SourceFilePath))).ToArray();
        if (entities.Length == 0) return;

        var names = ready.Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        var name = names.Length == 1 ? names[0]! : $"批量翻译 {DateTime.Now:yyyy-MM-dd HHmm}";
        var project = ProjectStore.Create(name, CurrentSourceLang, CurrentTargetLang, entities);
        ActiveTranslationProject = project;
        foreach (var task in tasks.Where(t => ready.Contains(NormalizeSourcePath(t.FilePath)))) _taskManager.AssignProject(task.Id, project.Id);
        RefreshTranslationProjects();
        StatusMessage = $"翻译完成并已归档为项目“{project.Name}”，现在可校对后回写并导出。";
    }

    private bool SaveActiveProject()
    {
        var project = ActiveTranslationProject;
        if (project == null) return false;
        var map = Entities.GroupBy(e => NormalizeSourcePath(e.SourceFilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Handle, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        foreach (var drawing in project.Drawings)
        {
            if (!map.TryGetValue(NormalizeSourcePath(drawing.SourcePath), out var entries)) continue;
            foreach (var saved in drawing.Entries)
            {
                if (!entries.TryGetValue(saved.Handle, out var entity)) continue;
                saved.TranslatedText = entity.TranslatedText ?? string.Empty;
                saved.Status = entity.Status;
                saved.Notes = entity.Notes ?? string.Empty;
                saved.GlossaryHit = entity.GlossaryHit;
                saved.ManuallyEdited = entity.Status == TranslationStatus.Reviewed || _proofreadingOriginals.ContainsKey(entity);
            }
        }
        try
        {
            ProjectStore.Save(project);
            RefreshTranslationProjects();
            return true;
        }
        catch (ProjectConflictException ex)
        {
            Log.Warning(ex, "翻译项目发生并发保存冲突，已阻止覆盖 {ProjectId}", project.Id);
            StatusMessage = "当前项目已被另一个窗口更新。为避免覆盖他人的修改，本次保存已停止；请重新打开项目后再合并更改。";
            DwgTranslator.App.Services.ToastService.Warning("项目已在其他窗口更新，本次保存未覆盖磁盘内容。");
            return false;
        }
    }

    [RelayCommand]
    private async Task OpenTranslationProjectAsync(TranslationProjectSummary? summary)
    {
        if (summary == null || IsProcessing || IsExporting || !ConfirmLeaveProofreading()) return;
        try
        {
            var project = ProjectStore.Load(summary.Id);
            var invalid = project.Drawings.Where(d => ProjectStore.ValidateSource(d) != ProjectSourceValidation.Valid).ToArray();
            if (invalid.Length > 0)
            {
                StatusMessage = $"项目有 {invalid.Length} 张源图缺失或内容已变化，已阻止套用旧句柄。请重新定位或重新导入。";
                return;
            }
            var dxfReader = _dxfReader;
            var loaded = await Task.Run(() =>
            {
                var result = new List<TextEntity>();
                foreach (var drawing in project.Drawings)
                {
                    var extracted = string.Equals(Path.GetExtension(drawing.SourcePath), ".dxf", StringComparison.OrdinalIgnoreCase)
                        ? dxfReader?.ExtractFromFile(drawing.SourcePath) ?? throw new IOException("DXF 读取器不可用。")
                        : _dwgReaderService.ExtractFromFile(drawing.SourcePath);
                    var saved = drawing.Entries.ToDictionary(e => e.Handle, StringComparer.Ordinal);
                    foreach (var entity in extracted)
                    {
                        entity.SourceFilePath = drawing.SourcePath;
                        if (!saved.TryGetValue(entity.Handle, out var entry)) continue;
                        entity.TranslatedText = entry.TranslatedText; entity.Status = entry.Status;
                        entity.Notes = entry.Notes; entity.GlossaryHit = entry.GlossaryHit;
                        result.Add(entity);
                    }
                }
                return result;
            });
            Entities.Clear(); foreach (var entity in loaded) Entities.Add(entity); InvalidateEntityIndex();
            RebuildDrawingFileList(project.Drawings.Select(d => d.SourcePath).ToArray());
            ActiveTranslationProject = project; CurrentSourceLang = project.SourceLanguage; CurrentTargetLang = project.TargetLanguage;
            ApplyFilter(); UpdateStatistics(); IsProofreading = true;
            StatusMessage = $"已打开项目“{project.Name}”，共 {loaded.Count} 条译文。";
        }
        catch (Exception ex) { Log.Warning(ex, "打开翻译项目失败"); StatusMessage = "翻译项目无法打开，原记录已保留。"; }
    }

    [RelayCommand]
    private void RenameActiveProject(string? name)
    {
        if (ActiveTranslationProject == null || string.IsNullOrWhiteSpace(name)) return;
        var id = ActiveTranslationProject.Id;
        if (!SaveActiveProject()) return;
        RenameProject(id, name);
        ActiveTranslationProject = ProjectStore.Load(id);
    }

    [RelayCommand]
    private void RenameSelectedProject()
    {
        if (SelectedTranslationProject == null || string.IsNullOrWhiteSpace(ProjectRenameName)) return;
        var id = SelectedTranslationProject.Id;
        var newName = ProjectRenameName.Trim();
        if (ActiveTranslationProject?.Id == id && !SaveActiveProject()) return;
        RenameProject(id, newName);
        SelectedTranslationProject = TranslationProjects.FirstOrDefault(item => item.Id == id);
        if (ActiveTranslationProject?.Id == id) ActiveTranslationProject = ProjectStore.Load(id);
        StatusMessage = $"项目已重命名为“{newName}”。";
    }

    private void RenameProject(string projectId, string name)
    {
        ProjectStore.Rename(projectId, name.Trim());
        RefreshTranslationProjects();
    }

    private void MigrateLegacyProofreading(IReadOnlyCollection<TextEntity> entities)
    {
        var legacy = Path.Combine(AccountDataDirectory, "proofreading.json");
        var marker = legacy + ".migrated";
        if (!File.Exists(legacy) || File.Exists(marker) || entities.Count == 0) return;
        try
        {
            ActiveTranslationProject = ProjectStore.Create("旧版校对记录", CurrentSourceLang, CurrentTargetLang, entities);
            File.WriteAllText(marker, ActiveTranslationProject.Id);
            RefreshTranslationProjects();
        }
        catch (Exception ex) { Log.Warning(ex, "旧校对记录迁移失败，原文件保留"); }
    }
}
