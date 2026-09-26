using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;
using Serilog;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    private TranslationProjectStore ProjectStore => new(AccountDataDirectory);
    private CancellationTokenSource? _projectAutosaveCts;
    private readonly SemaphoreSlim _projectSaveGate = new(1, 1);

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
                await SaveActiveProjectAsync(cts.Token);
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

    private void ClearActiveTranslationProjectContext()
    {
        // A debounced save captures no account/path; if it fires after an account/workspace switch,
        // ProjectStore would resolve against the NEW account directory and could persist the old
        // project's data under the wrong owner. Cancel it whenever workspace ownership changes.
        _projectAutosaveCts?.Cancel();
        _projectAutosaveCts?.Dispose();
        _projectAutosaveCts = null;
        _projectOpenVersion++;
        _restoredWorkspaceProjectId = null;
        ActiveTranslationProject = null;
        SelectTranslationProject(null);
        ProjectRenameName = string.Empty;
    }

    partial void OnActiveTranslationProjectChanged(TranslationProject? value)
    {
        OnPropertyChanged(nameof(HasActiveTranslationProject));
        if (value != null) ProjectRenameName = value.Name;
    }

    /// <summary>
    /// 选中即载入项目（用户批注 2026-09-23：「选择之后不会立即刷新列表」），
    /// 但载入后留在任务列表，逐张选择图纸进入详情/校对，不直接跳到单张图纸。
    /// _projectSelectionLocked 用来区分程序性回填（刷新列表、打开后重选），那种绝不能触发加载。
    /// </summary>
    partial void OnSelectedTranslationProjectChanged(TranslationProjectSummary? value)
    {
        if (value != null) ProjectRenameName = value.Name;
        if (_projectSelectionLocked || value == null) return;
        if (IsProcessing || IsTranslating || IsExporting)
        {
            SelectTranslationProject(ActiveTranslationProject?.Id);
            return;
        }

        // 选中即打开，但必须等待异步解析完成后再判断是否成功。旧实现通过 AsyncRelayCommand
        // 启动加载后立刻检查 ActiveTranslationProject，几乎必然仍是旧项目，于是下拉框会马上回跳。
        _ = OpenSelectedTranslationProjectAsync(value);
    }

    private async Task OpenSelectedTranslationProjectAsync(TranslationProjectSummary summary)
    {
        await OpenTranslationProjectAsync(summary);
        // 打开失败/用户取消时才拨回真正打开的项目；成功时也用程序性选择锁保持 UI 与工作区一致。
        if (ReferenceEquals(SelectedTranslationProject, summary))
            SelectTranslationProject(ActiveTranslationProject?.Id);
    }

    private bool _projectSelectionLocked;
    private int _projectOpenVersion;

    /// <summary>程序性回填项目选择（不触发加载）。</summary>
    private void SelectTranslationProject(string? id)
    {
        _projectSelectionLocked = true;
        try
        {
            SelectedTranslationProject = id == null
                ? null
                : TranslationProjects.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        }
        finally { _projectSelectionLocked = false; }
    }
    partial void OnProjectSearchChanged(string value) => RefreshTranslationProjects();

    private void RefreshTranslationProjects()
    {
        // 刷新会清空再填充，SelectedItem 随之中间变为 null——必须上锁，否则会被当成"用户改选"而触发加载。
        _projectSelectionLocked = true;
        try
        {
            TranslationProjects.Clear();
            foreach (var item in ProjectStore.List(ProjectSearch)) TranslationProjects.Add(item);
        }
        finally { _projectSelectionLocked = false; }
        // 刷新后保持"当前真正打开的项目"为选中项，让下拉与工作区始终一致。
        if (ActiveTranslationProject != null) SelectTranslationProject(ActiveTranslationProject.Id);
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
        // 翻译完成即可导出；校对是可选的人工复核（2026-09-22 用户批注「校对不是必须的」）。
        StatusMessage = $"翻译完成并已归档为项目“{project.Name}”，现在可以直接导出；需要人工复核时再进入校对。";
    }

    private bool SaveActiveProject()
    {
        var project = ActiveTranslationProject;
        if (project == null) return false;
        _projectSaveGate.Wait();
        try
        {
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
        finally { _projectSaveGate.Release(); }
    }

    private async Task<bool> SaveActiveProjectAsync(CancellationToken cancellationToken)
    {
        var project = ActiveTranslationProject;
        if (project == null) return false;
        await _projectSaveGate.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(project, ActiveTranslationProject) || IsProcessing || IsExporting) return false;
            var snapshot = CloneProject(project);
            var map = Entities.GroupBy(e => NormalizeSourcePath(e.SourceFilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Handle, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
            foreach (var drawing in snapshot.Drawings)
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

            var accountDirectory = AccountDataDirectory;
            await Task.Run(() => new TranslationProjectStore(accountDirectory).Save(snapshot), cancellationToken)
                .ConfigureAwait(false);
            // Project metadata is not observable; updating its revision before releasing the gate keeps
            // an explicit save from racing the completed background commit with a stale revision.
            project.Revision = snapshot.Revision;
            project.ModifiedAtUtc = snapshot.ModifiedAtUtc;
        }
        catch (ProjectConflictException ex)
        {
            Log.Warning(ex, "翻译项目自动保存发生并发冲突，已阻止覆盖 {ProjectId}", project.Id);
            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusMessage = "当前项目已被另一个窗口更新。为避免覆盖他人的修改，自动保存已停止；请重新打开项目后再合并更改。";
                DwgTranslator.App.Services.ToastService.Warning("项目已在其他窗口更新，自动保存未覆盖磁盘内容。");
            }));
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            Log.Warning(ex, "翻译项目自动保存失败");
            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => StatusMessage = "翻译项目自动保存失败，请检查磁盘空间和目录权限。修改仍保留在当前工作区。"));
            return false;
        }
        finally { _projectSaveGate.Release(); }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(ActiveTranslationProject, project)) RefreshTranslationProjects();
        });
        return true;
    }

    private static TranslationProject CloneProject(TranslationProject source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Revision = source.Revision,
        Id = source.Id,
        Name = source.Name,
        CreatedAtUtc = source.CreatedAtUtc,
        ModifiedAtUtc = source.ModifiedAtUtc,
        SourceLanguage = source.SourceLanguage,
        TargetLanguage = source.TargetLanguage,
        TranslationConfig = new(source.TranslationConfig),
        Drawings = source.Drawings.Select(drawing => new TranslationProjectDrawing
        {
            SourcePath = drawing.SourcePath,
            SourceFileName = drawing.SourceFileName,
            SourceSha256 = drawing.SourceSha256,
            Entries = drawing.Entries.Select(entry => new TranslationProjectEntry
            {
                Handle = entry.Handle,
                OriginalText = entry.OriginalText,
                RawText = entry.RawText,
                EntityType = entry.EntityType,
                TranslatedText = entry.TranslatedText,
                Status = entry.Status,
                Notes = entry.Notes,
                GlossaryHit = entry.GlossaryHit,
                ManuallyEdited = entry.ManuallyEdited
            }).ToList()
        }).ToList(),
        ExportHistory = source.ExportHistory.Select(export => new TranslationProjectExport
        {
            ExportedAtUtc = export.ExportedAtUtc,
            OutputDirectory = export.OutputDirectory,
            WritebackMode = export.WritebackMode,
            Files = export.Files.Select(file => new TranslationProjectExportFile
            {
                SourcePath = file.SourcePath,
                OutputPath = file.OutputPath,
                SuccessCount = file.SuccessCount,
                FailureCount = file.FailureCount,
                Skipped = file.Skipped,
                Message = file.Message
            }).ToList()
        }).ToList()
    };

    [RelayCommand]
    private async Task OpenTranslationProjectAsync(TranslationProjectSummary? summary)
    {
        if (summary == null || IsProcessing || IsExporting || !ConfirmLeaveProofreading()) return;

        // Opening a historical project replaces the visible workspace. Never hide a runnable queue
        // behind that project: RunAsync operates on TaskManager state, not on whatever rows happen to
        // be visible, so a hidden Pending/Paused task could otherwise be started by F5 later.
        if (_taskManager.Tasks.Any(t => t.Status is TranslationTaskStatus.Pending or TranslationTaskStatus.Paused))
        {
            StatusMessage = "当前还有待处理或已暂停的翻译任务。请先完成、取消或清除这些任务，再打开历史项目。";
            DwgTranslator.App.Services.ToastService.Warning(StatusMessage);
            return;
        }

        var openVersion = ++_projectOpenVersion;
        var workspaceVersion = _proofreadingWorkspaceVersion;
        try
        {
            var project = ProjectStore.Load(summary.Id);
            var invalid = project.Drawings.Where(d => ProjectStore.ValidateSource(d) != ProjectSourceValidation.Valid).ToArray();
            if (invalid.Length > 0)
            {
                StatusMessage = $"项目有 {invalid.Length} 张源图缺失或内容已变化，已阻止套用旧句柄。请重新定位或重新导入。";
                return;
            }
            var loaded = await LoadProjectEntitiesAsync(project, project.Drawings);
            // 用户在解析期间又选了另一个项目、导入了新图纸或开始了其它工作：
            // 旧请求不能晚到后把当前工作区整批覆盖。
            if (openVersion != _projectOpenVersion
                || workspaceVersion != _proofreadingWorkspaceVersion
                || IsProcessing || IsExporting || _taskManager.IsRunning)
                return;
            Entities.Clear(); foreach (var entity in loaded) Entities.Add(entity); InvalidateEntityIndex();
            RebuildDrawingFileList(project.Drawings.Select(d => d.SourcePath).ToArray());
            ActiveTranslationProject = project; CurrentSourceLang = project.SourceLanguage; CurrentTargetLang = project.TargetLanguage;
            ApplyFilter(); UpdateStatistics();
            IsProofreading = false;
            SelectedBatchTask = null;
            IsTaskDetailOpen = false;
            StatusMessage = $"已载入项目“{project.Name}”，共 {project.Drawings.Count} 张图纸、{loaded.Count} 条译文。请在任务列表中逐张查看。";
        }
        catch (Exception ex)
        {
            if (openVersion == _projectOpenVersion)
            {
                Log.Warning(ex, "打开翻译项目失败");
                StatusMessage = "翻译项目无法打开，原记录已保留。";
            }
        }
    }

    [RelayCommand]
    private void DeleteSelectedTranslationProject()
    {
        var summary = SelectedTranslationProject;
        if (summary == null) return;
        if (IsProcessing || IsTranslating || IsExporting)
        {
            StatusMessage = "当前有任务正在运行，请完成后再删除历史项目。";
            DwgTranslator.App.Services.ToastService.Warning(StatusMessage);
            return;
        }

        var answer = DwgTranslator.App.Views.PromptDialog.Show(
            $"确定从历史项目中删除“{summary.Name}”吗？\n\n只删除翻译项目归档，不删除源图纸和已导出的文件。",
            "删除历史项目", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var removesActiveProject = ActiveTranslationProject?.Id == summary.Id;
        if (removesActiveProject && !ConfirmLeaveProofreading()) return;
        if (removesActiveProject)
        {
            _projectAutosaveCts?.Cancel();
            _projectAutosaveCts?.Dispose();
            _projectAutosaveCts = null;
        }

        try
        {
            _projectSaveGate.Wait();
            try
            {
                var linkedTasks = _taskManager.Tasks
                    .Where(task => string.Equals(task.ProjectId, summary.Id, StringComparison.Ordinal))
                    .ToArray();
                foreach (var task in linkedTasks) task.ProjectId = null;
                try { _taskManager.EnsureAccountStoreSaved(); }
                catch
                {
                    foreach (var task in linkedTasks) task.ProjectId = summary.Id;
                    throw;
                }

                try { ProjectStore.Delete(summary.Id); }
                catch
                {
                    foreach (var task in linkedTasks) task.ProjectId = summary.Id;
                    try { _taskManager.EnsureAccountStoreSaved(); }
                    catch (Exception saveException) { Log.Warning(saveException, "恢复历史项目引用失败：{ProjectId}", summary.Id); }
                    throw;
                }
                if (removesActiveProject) ActiveTranslationProject = null;
            }
            finally { _projectSaveGate.Release(); }
            RefreshTranslationProjects();
            if (removesActiveProject) ScheduleWorkspaceSessionSave();
            ProjectRenameName = string.Empty;
            StatusMessage = $"已从历史记录删除项目“{summary.Name}”；源图纸和导出文件保持不变。";
            DwgTranslator.App.Services.ToastService.Success("历史项目已删除。图纸文件未删除。");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "删除历史翻译项目失败 {ProjectId}", summary.Id);
            StatusMessage = "历史项目删除失败，原有记录保持不变。";
            DwgTranslator.App.Services.ToastService.Error(StatusMessage);
        }
    }

    /// <summary>
    /// 按句柄把项目里归档的译文套回磁盘上重新解析出来的实体。
    /// 必须重新解析源图而不是直接信任归档里的文本：写回需要几何与位置，句柄也只是在"源图未变"时才有意义
    /// （调用方负责先做 ValidateSource）。归档里没有的句柄直接跳过，不臆造译文。
    /// </summary>
    private async Task<List<TextEntity>> LoadProjectEntitiesAsync(
        TranslationProject project, IReadOnlyCollection<TranslationProjectDrawing> drawings)
    {
        var dxfReader = _dxfReader;
        return await Task.Run(() =>
        {
            var result = new List<TextEntity>();
            foreach (var drawing in drawings)
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
    }

    /// <summary>
    /// 启动时把上次翻译的译文补回工作区。
    /// 用户报告（2026-09-22）：「我翻译之后，退出软件，再次打开就看不到译文了！又要重新翻译啊！」
    /// 根因是两条持久化路径不对称：翻译成功会自动把译文归档进项目库
    /// （<see cref="ArchiveTranslationRun"/> → projects/&lt;id&gt;/project.json），此后"保存校对"写的也是项目库
    /// （<see cref="TrySaveProofreading"/> 里 ActiveTranslationProject != null 的分支）；
    /// 而启动恢复 <see cref="RestoreSavedProofreadingAsync"/> 只读 proofreading.json —— 那份文件走项目库的
    /// 流程里从头到尾没被写过。于是 tasks.json 能把"待导出"这个状态恢复回来，译文却是空的。
    /// 这里按恢复出来的任务上的 ProjectId 静默加载译文：不打开校对视图、不覆盖用户正在编辑的内容。
    /// </summary>
    private async Task RestoreActiveTranslationProjectAsync()
    {
        if (Entities.Count != 0 || HasUnsavedProofreading || _taskManager.IsRunning || IsExporting || IsProcessing) return;

        // 只认"翻译已经结束"的行：待处理/已暂停的图纸点"继续处理"会重新产出译文，
        // 先灌一份旧的进去只会让两个来源打架。归档时也只给这些状态的任务挂过 ProjectId。
        string? projectId = _restoredWorkspaceProjectId;
        HashSet<string> wanted;

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            projectId = projectId.Trim();
            wanted = DrawingFiles.Select(row => NormalizeSourcePath(row.FullPath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var settled = DrawingFiles
                .Where(row => row.Task is { ProjectId: not null } task
                    && task.Status is TranslationTaskStatus.ReadyForReview or TranslationTaskStatus.Completed)
                .ToArray();
            if (settled.Length == 0) return;

            projectId = settled.OrderByDescending(row => row.Task!.UpdatedAt)
                .Select(row => row.Task!.ProjectId!.Trim()).First();
            wanted = settled
                .Where(row => string.Equals(row.Task!.ProjectId!.Trim(), projectId, StringComparison.Ordinal))
                .Select(row => NormalizeSourcePath(row.FullPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        TranslationProject project;
        try { project = ProjectStore.Load(projectId); }
        catch (Exception ex) { Log.Warning(ex, "启动恢复译文失败：项目 {ProjectId} 无法读取", projectId); return; }
        var candidates = project.Drawings.Where(d => wanted.Contains(NormalizeSourcePath(d.SourcePath))).ToArray();
        // 源图缺失或已被改写时不套用旧句柄（与用户主动"打开项目"同一道校验），但只跳过这几张，
        // 不像主动打开那样整单拒绝 —— 否则用户又会看到"译文全没了"。
        var valid = candidates.Where(d => ProjectStore.ValidateSource(d) == ProjectSourceValidation.Valid).ToArray();
        if (valid.Length == 0) return;

        List<TextEntity> loaded;
        try { loaded = await LoadProjectEntitiesAsync(project, valid); }
        catch (Exception ex) { Log.Warning(ex, "启动恢复译文失败：项目 {ProjectId} 的源图无法解析", projectId); return; }
        if (loaded.Count == 0) return;
        // 异步解析期间用户可能已经导入了图纸或开始翻译，那就不要用旧译文盖掉当前工作区。
        if (Entities.Count != 0 || HasUnsavedProofreading || IsProcessing || IsExporting || _taskManager.IsRunning) return;

        Entities.Clear(); foreach (var entity in loaded) Entities.Add(entity); InvalidateEntityIndex();
        ActiveTranslationProject = project;
        _restoredWorkspaceProjectId = null;
        CurrentSourceLang = project.SourceLanguage; CurrentTargetLang = project.TargetLanguage;
        ApplyFilter(); UpdateStatistics();

        var skipped = candidates.Length - valid.Length;
        StatusMessage = skipped == 0
            ? $"已恢复上次翻译的 {loaded.Count} 条译文（项目“{project.Name}”），可直接校对或导出。"
            : $"已恢复上次翻译的 {loaded.Count} 条译文（项目“{project.Name}”）；另有 {skipped} 张图纸已变化或缺失，未套用旧译文。";
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
