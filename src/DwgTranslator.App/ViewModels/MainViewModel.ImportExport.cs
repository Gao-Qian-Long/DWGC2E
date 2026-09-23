using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    #region DWG/DXF Import

    [RelayCommand]
    private async Task ImportDwgAsync()
    {
        if (IsProcessing) { DwgTranslator.App.Services.ToastService.Warning("当前任务正在执行，完成或取消后再添加图纸。"); return; }
        if (IsLoggingIn) { DwgTranslator.App.Services.ToastService.Info("账户正在切换，请稍后再添加图纸。"); return; }

        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("FilterCadFiles"),
            Title = Strings.Get("DialogTitleSelectDwg"),
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        await ImportCadFilesAsync(dialog.FileNames).ConfigureAwait(true);
    }

    /// <summary>
    /// File types accepted by drag-and-drop and by the command line.
    /// </summary>
    public static readonly string[] SupportedCadExtensions = [".dwg", ".dxf"];
    // ClosedXML reads OpenXML workbooks only; advertising legacy binary .xls caused a guaranteed
    // import failure after drag-and-drop accepted the file.
    public static readonly string[] SupportedExcelExtensions = [".xlsx"];

    /// <summary>
    /// True when a dropped path has an extension the application can import.
    /// </summary>
    public static bool IsSupportedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return SupportedCadExtensions.Contains(extension) || SupportedExcelExtensions.Contains(extension);
    }

    /// <summary>
    /// Entry point shared by drag-and-drop and by files passed on the command line: splits
    /// the dropped paths by type and imports each group. Unknown extensions are reported
    /// instead of being silently ignored, so a wrong drop is never a no-op.
    /// </summary>
    public async Task ImportDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsProcessing) { DwgTranslator.App.Services.ToastService.Warning("当前任务正在执行，已忽略本次拖入；完成或取消后再添加图纸。"); return; }
        if (IsLoggingIn) { DwgTranslator.App.Services.ToastService.Info("账户正在切换，已忽略本次拖入；请稍后重试。"); return; }

        var cadFiles = new List<string>();
        var excelFiles = new List<string>();
        var unsupported = new List<string>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (File.Exists(path) == false) { unsupported.Add(Path.GetFileName(path)); continue; }
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (SupportedCadExtensions.Contains(extension)) cadFiles.Add(path);
            else if (SupportedExcelExtensions.Contains(extension)) excelFiles.Add(path);
            else unsupported.Add(Path.GetFileName(path));
        }

        if (cadFiles.Count == 0 && excelFiles.Count == 0)
        {
            StatusMessage = unsupported.Count > 0
                ? Strings.Get("StatusDropUnsupported", string.Join(", ", unsupported.Take(3)))
                : Strings.Get("StatusDropNothing");
            DwgTranslator.App.Services.ToastService.Warning(StatusMessage);
            return;
        }

        var cadWorkspaceCommitted = true;
        if (cadFiles.Count > 0)
            cadWorkspaceCommitted = await ImportCadFilesAsync(cadFiles).ConfigureAwait(true);

        // XLSX is a review overlay for the CAD workspace from the same drop. If every CAD file
        // failed, applying the spreadsheet to the previously open workspace could corrupt unrelated
        // translations when handles happen to overlap.
        if (excelFiles.Count > 0 && cadFiles.Count > 0 && !cadWorkspaceCommitted)
        {
            DwgTranslator.App.Services.ToastService.Warning(
                "本次图纸导入全部失败，未将同批 XLSX 套用到原工作区。请修复图纸后重新拖入。");
        }
        else
        {
            foreach (var excelFile in excelFiles)
            {
                if (IsProcessing || IsLoggingIn) break;
                await ImportExcelFilesAsync(excelFile).ConfigureAwait(true);
            }
        }

        if (unsupported.Count > 0)
        {
            var preview = string.Join("、", unsupported.Take(3));
            var suffix = unsupported.Count > 3 ? $" 等 {unsupported.Count} 个" : string.Empty;
            DwgTranslator.App.Services.ToastService.Warning($"已忽略不支持的文件：{preview}{suffix}。仅支持 DWG / DXF / XLSX。");
            Log.Warning("Dropped files ignored ({Count}): {Files}", unsupported.Count, string.Join("; ", unsupported));
        }
    }

    /// <summary>
    /// Imports one or more DWG/DXF files. Shared by the toolbar dialog and drag-and-drop.
    /// </summary>
    public async Task<bool> ImportCadFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (!ConfirmLeaveProofreading()) return false;
        if (IsProcessing || IsLoggingIn) return false;
        if (filePaths.Count == 0) return false;
        var workspaceCommitted = false;

        // Resolve DXF reader before entering background thread（构造函数注入，不再走静态服务定位）
        var dxfReader = _dxfReader;

        await RunWithProgress(async () =>
        {
            var importErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allEntities = await Task.Run(() =>
            {
                var result = new List<TextEntity>();
                foreach (var path in filePaths)
                {
                    try
                    {
                        var extension = Path.GetExtension(path).ToLowerInvariant();
                        List<TextEntity> entities = extension == ".dxf"
                            ? dxfReader?.ExtractFromFile(path) ?? new List<TextEntity>()
                            : _dwgReaderService.ExtractFromFile(path);

                        var fullPath = Path.GetFullPath(path);
                        foreach (var e in entities)
                        {
                            // Stamp source file so multi-file export can scope by drawing.
                            // Handles are only unique within a single DWG/DXF.
                            e.SourceFilePath = fullPath;
                            e.Notes = $"Source: {Path.GetFileNameWithoutExtension(path)}";
                        }
                        result.AddRange(entities);
                    }
                    catch (Exception ex)
                    {
                        importErrors[path] = ex.Message;
                    }
                }
                return result;
            });

            var successfulPaths = filePaths.Where(p => !importErrors.ContainsKey(p)).ToList();

            // Replace-workspace semantics are intentional, but replacement must be transactional:
            // a completely failed import must never destroy the user's existing queue/entities.
            // Only commit the new workspace after at least one requested drawing was parsed successfully.
            if (successfulPaths.Count == 0)
            {
                StatusMessage = Strings.Get("StatusCadImportFailed");
                Log.Warning("CAD import failed for every requested file ({Count}): {Errors}", importErrors.Count,
                    string.Join("; ", importErrors.Select(pair => $"{Path.GetFileName(pair.Key)}: {pair.Value}")));
                DwgTranslator.App.Views.PromptDialog.Show(Strings.Get("MsgCadImportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _proofreadingWorkspaceVersion++;
            ClearActiveTranslationProjectContext();

            // Replace workspace semantics: old queued drawings must not execute against hidden rows.
            _taskManager.Clear(includeUnfinished: true);
            Entities.Clear();
            InvalidateEntityIndex();
            foreach (var entity in allEntities)
                Entities.Add(entity);

            RebuildDrawingFileList(filePaths, importErrors);
            // 导入即入队：任务层是主链路的唯一入口。同一张图纸重复入队会被任务层合并，
            // 不会重复跑；导入失败的文件不入队（否则队列里会出现一张必然失败的图纸）。
            foreach (var path in successfulPaths)
                _taskManager.Enqueue(path);
            AttachTasksToRows();
            ApplyFilter();
            UpdateStatistics();
            workspaceCommitted = true;

            if (importErrors.Count > 0)
            {
                StatusMessage = Strings.Get("StatusImportPartialFail", allEntities.Count, importErrors.Count);
                Log.Warning("Import errors ({Count}): {Errors}", importErrors.Count,
                    string.Join("; ", importErrors.Select(pair => $"{Path.GetFileName(pair.Key)}: {pair.Value}")));
            }
            else
            {
                StatusMessage = Strings.Get("StatusImported", allEntities.Count, successfulPaths.Count);
            }
        }, Strings.Get("OperationImporting"), "StatusCadImportFailed", "MsgCadImportError");
        return workspaceCommitted;
    }

    #endregion

    #region DWG/DXF Export

    [RelayCommand]
    private async Task ExportDwgAsync()
    {
        if (IsProcessing || IsLoggingIn || !ConfirmLeaveProofreading()) return;

        // Preserve internal mappings from older application/Excel sessions too.
        foreach (var metadata in Entities.Where(e => AttributeTranslationPolicy.IsMetadataHandle(e.Handle)))
        {
            metadata.Status = TranslationStatus.Skipped;
            metadata.TranslatedText = metadata.RawText;
        }

        if (_config.LicensingEnabled && !_licenseService.CanExecuteOperation())
        {
            StatusMessage = Strings.Get("StatusLicenseRequired");
            PromptForActivation();
            return;
        }

        var requestedSources = DrawingFiles.Count > 0
            ? DrawingFiles.Where(f => f.IsIncludedForExport && string.IsNullOrWhiteSpace(f.ImportError))
                .Select(f => f.FullPath).ToList()
            : BatchExportPlanner.GetKnownSources(Entities).ToList();

        // Explicit export includes reviewed results even after a task has written an earlier version.
        // Destination planning below still applies duplicate policy and source-file protection.
        if (requestedSources.Count == 0)
        {
            StatusMessage = DrawingFiles.Count > 0
                ? "请至少勾选一张要导出的图纸。"
                : "缺少源图纸信息，请重新拖入 DWG/DXF 后再导出。";
            DwgTranslator.App.Views.PromptDialog.Show(StatusMessage, Strings.Get("MsgTitleNoData"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var translatedEntities = Entities
            .Where(e => requestedSources.Contains(
                NormalizeSourcePath(e.SourceFilePath), StringComparer.OrdinalIgnoreCase))
            .Where(e => e.Status is TranslationStatus.Translated or TranslationStatus.Reviewed
                or TranslationStatus.WritebackSuccess or TranslationStatus.WritebackFailed)
            .ToList();

        var writebackGlossary = EffectiveGlossary.Resolve(_glossaryService.GetAllEntries(), CurrentSourceLang, CurrentTargetLang);
        var invalidTranslations = translatedEntities
            .Where(e => !TranslationQualityValidator.IsAcceptableCadText(
                e.PlainText, e.TranslatedText, CurrentSourceLang, CurrentTargetLang, writebackGlossary))
            .ToList();
        if (invalidTranslations.Count > 0)
        {
            foreach (var entity in invalidTranslations)
                entity.Status = TranslationStatus.TranslationFailed;
            UpdateStatistics();
            ApplyFilter();
            StatusMessage = $"发现 {invalidTranslations.Count} 条未完整翻译内容，已阻止导出并标记为失败。";
            DwgTranslator.App.Views.PromptDialog.Show(
                $"检测到 {invalidTranslations.Count} 条仍包含原文的结果。\n\n程序已阻止这些内容写入图纸，请点击“重试失败”后再导出。",
                "翻译完整性检查", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (translatedEntities.Count == 0)
        {
            StatusMessage = Strings.Get("StatusNoTranslatedText");
            DwgTranslator.App.Views.PromptDialog.Show(Strings.Get("MsgNoTranslatedData"),
                Strings.Get("MsgTitleNoData"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The imported file list is the source of truth. The user selects an output folder once;
        // the application never asks them to find each source drawing again.
        await ExportAllSourcesAsync(requestedSources, translatedEntities);
    }

    /// <summary>
    /// Exports every imported drawing that has translations, writing one "_translated" file per
    /// source into a single chosen folder.
    ///
    /// A drawing that is not fully translated, has no translations, or would overwrite its own
    /// source is reported and skipped rather than stopping the rest of the batch: blocking the
    /// whole set was what made a multi-drawing import unusable.
    /// </summary>
    private async Task ExportAllSourcesAsync(List<string> sources, List<TextEntity> translatedEntities)
    {
        var targets = sources.Where(File.Exists).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = Strings.Get("StatusNoTranslatedText");
            DwgTranslator.App.Views.PromptDialog.Show(Strings.Get("MsgNoTranslatedData"),
                Strings.Get("MsgTitleNoData"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 默认输出目录 = <安装目录>\exports（2026-10-02 用户决定）；安装区不可写时退回
        // 源图纸旁边（<源目录>\<DefaultOutputFolderName>）。只有用户在设置里显式选过的
        // 目录才会覆盖这个默认值。
        var targetFolder = AccountWorkspace.OutputDirectoryFor(
            _config, App.AppDataDir, Path.GetDirectoryName(targets[0]), DefaultOutputFolderName, App.InstallDir);
        if (string.IsNullOrWhiteSpace(targetFolder) || !Path.IsPathFullyQualified(targetFolder))
        {
            // 只有算不出源目录时才回到"让用户选一次"，并且选完就固化成本账号的默认目录。
            var folderDialog = new OpenFolderDialog
            {
                Title = $"首次回写：选择并保存账号默认导出目录（将导出 {targets.Count} 张图纸）",
                InitialDirectory = Path.GetDirectoryName(targets[0]) ?? string.Empty
            };
            if (folderDialog.ShowDialog() != true)
            {
                StatusMessage = "未设置默认导出目录，未写入任何文件。";
                return;
            }
            targetFolder = Path.GetFullPath(folderDialog.FolderName);
        }
        _config.AccountOutputDirectories ??= new();
        var accountKey = string.IsNullOrWhiteSpace(_config.ActiveAccountId) ? "guest" : _config.ActiveAccountId;
        if (!string.Equals(_config.AccountOutputDirectories.TryGetValue(accountKey, out var saved) ? saved : null,
                targetFolder, StringComparison.OrdinalIgnoreCase))
        {
            // 与 ProductDataDirectory 的约定一致：安装/数据区下需要预先存在的源目录才迁移，缺失即跳过。
            var parent = Path.GetDirectoryName(targetFolder);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)) Directory.CreateDirectory(targetFolder);
            _config.AccountOutputDirectories[accountKey] = targetFolder;
            SettingsStore.Update(_settingsPath ?? Path.Combine(App.AppDataDir, "settings.json"), latest =>
            {
                latest.AccountOutputDirectories ??= new();
                latest.AccountOutputDirectories[accountKey] = targetFolder;
                latest.ExportDirectory = targetFolder;
            });
        }
        _config.ExportDirectory = targetFolder;
        OnPropertyChanged(nameof(OutputDirectoryText));
        OnPropertyChanged(nameof(WorkspaceActionHintText));
        // The mode dialog is always shown. Without a CAD host it still owns a real decision - the
        // per-batch temporary export directory - and it hides the mode radio group itself, so the
        // only option a user sees there is one they can actually act on.
        var modeDialog = new Views.ExportModeDialog(_autoCadInteropService.IsAutoCADAvailable(_config), isTranslation: true)
        {
            Owner = Application.Current.MainWindow
        };
        if (modeDialog.ShowDialog() != true)
        {
            StatusMessage = Strings.Get("StatusExportCancelled");
            return;
        }
        var mode = modeDialog.SelectedMode;
        if (!string.IsNullOrWhiteSpace(modeDialog.TemporaryDirectory))
            targetFolder = modeDialog.TemporaryDirectory;
        Directory.CreateDirectory(targetFolder);

        // 导出规划只读这四个字段（OutputPathResolver / 重复策略 / 备份开关），
        // 用浅拷贝替代整份 AppConfig 的 JSON 序列化往返。
        var exportConfig = new AppConfig
        {
            OutputNamingPattern = _config.OutputNamingPattern,
            ExportDirectory = targetFolder,
            BackupSourceBeforeWrite = _config.BackupSourceBeforeWrite,
            DuplicatePolicy = _config.DuplicatePolicy
        };
        var exportPlan = BatchExportPlanner.CreateExportPlan(targets, CurrentTargetLang, exportConfig);
        var plannedBySource = exportPlan.Results.ToDictionary(
            result => NormalizeSourcePath(result.SourcePath),
            StringComparer.OrdinalIgnoreCase);
        var overwriteFiles = exportPlan.Results.Where(r => r.Resolution == DuplicateResolution.OverwriteExisting && File.Exists(r.OutputPath)).Select(r => r.OutputPath).ToArray();
        if (overwriteFiles.Length > 0 && Views.PromptDialog.Show(
            "即将覆盖以下输出文件（源图始终不会覆盖）：\n\n" + string.Join("\n", overwriteFiles.Select(Path.GetFileName)),
            "确认覆盖输出文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (exportPlan.Skipped.Count > 0)
        {
            StatusMessage = "已跳过 " + exportPlan.Skipped.Count + " 个已存在的输出文件（重名策略：" + exportConfig.DuplicatePolicy + "）";
            Log.Information("Export skipped {Count} existing outputs: {Reasons}",
                exportPlan.Skipped.Count, string.Join("; ", exportPlan.Skipped.Select(s => s.Reason)));
        }

        if (mode == Views.ExportModeDialog.ExportMode.AutoCAD)
        {
            var preflight = await EnsureCadPluginReadyAsync();
            if (preflight == CadPreflightOutcome.Cancelled) return;
            if (preflight == CadPreflightOutcome.UseOffline) mode = Views.ExportModeDialog.ExportMode.Offline;
        }

        var written = new List<(string Source, string Path, int Count, DuplicateResolution Resolution)>();
        var failed = new List<string>();
        var skipped = new List<string>();

        IsExporting = true;
        try
        {
            await RunWithProgress(async () =>
            {
                IsCancellationRequested = false;
                _exportCts = new CancellationTokenSource();
                ProgressValue = 0;
                double step = 100.0 / targets.Count;

                for (int index = 0; index < targets.Count; index++)
                {
                    if (_exportCts.IsCancellationRequested) break;
                    var source = targets[index];
                    var name = Path.GetFileName(source);
                    StatusMessage = $"正在导出 {index + 1}/{targets.Count}：{name}";
                    ProgressValue = index * step;

                    var mine = translatedEntities
                        .Where(e => string.Equals(
                            NormalizeSourcePath(e.SourceFilePath), source, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (mine.Count == 0)
                    {
                        skipped.Add($"{name}（没有可写入的译文）");
                        ProgressValue = (index + 1) * step;
                        continue;
                    }

                    var unfinished = Entities.Count(e =>
                        string.Equals(NormalizeSourcePath(e.SourceFilePath), source, StringComparison.OrdinalIgnoreCase) &&
                        e.Status is TranslationStatus.Pending or TranslationStatus.TranslationFailed
                            or TranslationStatus.GlossaryMatched);
                    if (unfinished > 0)
                    {
                        // 用户批注（2026-09-23）：只报"3 条未完成"没用——用户不知道是哪 3 条、该去改什么。
                        // 这里按分类计数并列出句柄（最多 6 个，其余折叠），日志里给全量。
                        var blockers = Entities.Where(e =>
                            string.Equals(NormalizeSourcePath(e.SourceFilePath), source, StringComparison.OrdinalIgnoreCase) &&
                            e.Status is TranslationStatus.Pending or TranslationStatus.TranslationFailed
                                or TranslationStatus.GlossaryMatched).ToList();
                        var pendingCount = blockers.Count(e => e.Status == TranslationStatus.Pending);
                        var failedCount = blockers.Count(e => e.Status == TranslationStatus.TranslationFailed);
                        var glossaryCount = blockers.Count(e => e.Status == TranslationStatus.GlossaryMatched);
                        var parts = new List<string>();
                        if (pendingCount > 0) parts.Add($"待翻译 {pendingCount}");
                        if (failedCount > 0) parts.Add($"翻译失败 {failedCount}");
                        if (glossaryCount > 0) parts.Add($"术语命中待确认 {glossaryCount}");
                        var handles = string.Join("、", blockers.Take(6).Select(e => "#" + e.Handle));
                        if (blockers.Count > 6) handles += $"…等 {blockers.Count} 条";
                        skipped.Add($"{name}（{string.Join("，", parts)}：{handles}）");
                        Log.Warning("Batch export skipped {File}: {Count} unfinished entities ({Detail}) handles: {Handles}",
                            name, unfinished, string.Join(",", parts),
                            string.Join(", ", blockers.Select(e => $"{e.Handle}[{e.Status}]")));
                        ProgressValue = (index + 1) * step;
                        continue;
                    }

                    if (!plannedBySource.TryGetValue(source, out var planned))
                    {
                        failed.Add($"{name}：未找到输出规划结果");
                        ProgressValue = (index + 1) * step;
                        continue;
                    }
                    if (planned.ShouldSkip)
                    {
                        skipped.Add($"{name}（{planned.Reason}）");
                        ProgressValue = (index + 1) * step;
                        continue;
                    }

                    var dest = planned.OutputPath;
                    if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add($"{name}（输出文件名与源文件相同）");
                        ProgressValue = (index + 1) * step;
                        continue;
                    }

                    try
                    {
                        var (result, _, cancelled) = await ExecuteWritebackWithMode(mode, source, dest, mine, planned.BackupPath);
                        if (cancelled) break;
                        if (result.SuccessCount > 0)
                        {
                            if (result.FailCount == 0 && result.SuccessCount >= mine.Count)
                                foreach (var entity in mine)
                                    entity.Status = TranslationStatus.WritebackSuccess;
                            written.Add((source, dest, result.SuccessCount, planned.Resolution));
                            if (result.FailCount > 0)
                                failed.Add($"{name}：{result.FailCount} 条未写入 —— {string.Join("; ", result.Errors.Take(2))}");
                        }
                        else
                        {
                            failed.Add($"{name}：{string.Join("; ", result.Errors.Take(2))}");
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        failed.Add($"{name}：{ex.Message}");
                        Log.Error(ex, "Batch export failed for {File}", source);
                    }

                    ProgressValue = (index + 1) * step;
                }

                if (written.Count > 0 && _config.LicensingEnabled)
                    ConsumeLicenseForExport();
                ProgressValue = 100;

                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"已导出 {written.Count} 张图纸到：{targetFolder.Replace('\\', '/')}");
                foreach (var item in written)
                    summary.AppendLine($"    ✓ {Path.GetFileName(item.Path)}（{item.Count} 条译文，{DescribeDuplicateResolution(item.Resolution)}）");
                if (skipped.Count > 0)
                {
                    summary.AppendLine().AppendLine($"跳过 {skipped.Count} 张：");
                    foreach (var item in skipped) summary.AppendLine($"    – {item}");
                }
                if (failed.Count > 0)
                {
                    summary.AppendLine().AppendLine($"失败 {failed.Count} 张：");
                    foreach (var item in failed) summary.AppendLine($"    ✗ {item}");
                }

                StatusMessage = $"批量导出完成：成功 {written.Count} 张，跳过 {skipped.Count} 张，失败 {failed.Count} 张";
                // 结果提示改走 Toast（§29）：详细清单进日志，界面只给一行结论，不再弹阻塞对话框
                Log.Information("导出结果：{Summary}", summary.ToString().Replace(Environment.NewLine, " | "));
                if (written.Count > 0 && failed.Count == 0)
                    DwgTranslator.App.Services.ToastService.Success($"导出完成：成功 {written.Count} 张到 {targetFolder}");
                else if (written.Count > 0)
                    DwgTranslator.App.Services.ToastService.Warning($"导出完成：成功 {written.Count} 张，失败 {failed.Count} 张");
                else
                    DwgTranslator.App.Services.ToastService.Error($"导出失败：{failed.Count} 张未写出，详见日志");

                if (written.Count > 0)
                {
                    // Keep the task row fast and usable after export while project history remains authoritative.
                    foreach (var item in written)
                    {
                        var task = _taskManager.Tasks
                            .Where(candidate => string.Equals(
                                NormalizeSourcePath(candidate.FilePath),
                                NormalizeSourcePath(item.Source),
                                StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(candidate => candidate.CreatedAt)
                            .FirstOrDefault();
                        if (task != null)
                            _taskManager.RecordExportPath(task.Id, item.Path);
                    }
                }

                if (ActiveTranslationProject != null && written.Count > 0)
                {
                    var record = new TranslationProjectExport { OutputDirectory = targetFolder, WritebackMode = mode.ToString() };
                    foreach (var item in written) record.Files.Add(new TranslationProjectExportFile { SourcePath = item.Source, OutputPath = item.Path, SuccessCount = item.Count, Message = item.Resolution.ToString() });
                    ProjectStore.AppendExport(ActiveTranslationProject.Id, record);
                    ActiveTranslationProject = ProjectStore.Load(ActiveTranslationProject.Id);
                }
                if (written.Count > 0)
                {
                    var resultWindow = new Views.SavedOutputsWindow(written.Select(item => item.Path).ToArray()) { Owner = Application.Current.MainWindow };
                    resultWindow.Show();
                }
                ApplyFilter();
                UpdateStatistics();
            }, Strings.Get("OperationExporting"), "StatusCadExportFailed", "MsgCadExportError");
        }
        finally
        {
            IsExporting = false;
        }
    }

    private static string DescribeDuplicateResolution(DuplicateResolution resolution) => resolution switch
    {
        DuplicateResolution.OverwriteExisting => "已覆盖既有输出",
        DuplicateResolution.Renamed => "已自动重命名",
        _ => "新文件"
    };
    private enum CadPreflightOutcome { Ready, UseOffline, Cancelled }

    private async Task<CadPreflightOutcome> EnsureCadPluginReadyAsync()
    {
        var cadPath = ResolveCadInstallPath();
        if (string.IsNullOrWhiteSpace(cadPath))
        {
            var choice = Views.PromptDialog.Show("未找到可用 CAD。选择“是”改用离线导出，选择“取消”返回。", "CAD 精准回写不可用", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            return choice == MessageBoxResult.OK ? CadPreflightOutcome.UseOffline : CadPreflightOutcome.Cancelled;
        }
        while (true)
        {
            var status = CadPluginInstaller.Inspect(cadPath, CadPluginDirectory);
            if (status.Ready) return CadPreflightOutcome.Ready;
            var running = new[] { "acad", "acadlt", "gcad" }.Any(name => System.Diagnostics.Process.GetProcessesByName(name).Length > 0);
            if (running)
            {
                var choice = Views.PromptDialog.Show("CAD 插件缺失、过期或自动加载配置异常，且检测到 CAD 正在运行。\n\n请先保存图纸并关闭 CAD。\n“是”＝重新检测；“否”＝改用离线导出；“取消”＝停止导出。\nAPP 不会强制关闭 CAD。",
                    "CAD 插件预检", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.No) return CadPreflightOutcome.UseOffline;
                if (choice != MessageBoxResult.Yes) return CadPreflightOutcome.Cancelled;
                continue;
            }
            var repair = Views.PromptDialog.Show("CAD 已关闭。精准回写需要修复插件并重新校验版本、文件哈希和自动加载配置。\n\n“是”＝立即修复；“否”＝改用离线导出；“取消”＝停止导出。",
                "修复 CAD 插件", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (repair == MessageBoxResult.No) return CadPreflightOutcome.UseOffline;
            if (repair != MessageBoxResult.Yes) return CadPreflightOutcome.Cancelled;
            var result = await Task.Run(() => CadPluginInstaller.Install(cadPath, CadPluginDirectory));
            if (!result.Success)
            {
                Log.Error("CAD plugin repair failed: {Summary}; {Error}; Steps={Steps}", result.Summary, result.Error, string.Join(" | ", result.Steps));
                var failure = Views.PromptDialog.Show("插件修复失败：" + result.Summary + "\n\n" + result.Error + "\n失败步骤：" + string.Join(" → ", result.Steps) + "\n日志目录：" + LogDirectory + "\n\n“是”＝重新检测；“否”＝改用离线导出；“取消”＝停止导出。",
                    "CAD 插件修复失败", MessageBoxButton.YesNoCancel, MessageBoxImage.Error);
                if (failure == MessageBoxResult.No) return CadPreflightOutcome.UseOffline;
                if (failure != MessageBoxResult.Yes) return CadPreflightOutcome.Cancelled;
                continue;
            }
            status = CadPluginInstaller.Inspect(cadPath, CadPluginDirectory);
            if (status.Ready) return CadPreflightOutcome.Ready;
            var recheck = Views.PromptDialog.Show("插件文件已复制，但版本、哈希或自动加载配置复检仍未通过。\n日志目录：" + LogDirectory + "\n\n“是”＝重新检测；“否”＝改用离线导出；“取消”＝停止导出。",
                "CAD 插件复检失败", MessageBoxButton.YesNoCancel, MessageBoxImage.Error);
            if (recheck == MessageBoxResult.No) return CadPreflightOutcome.UseOffline;
            if (recheck != MessageBoxResult.Yes) return CadPreflightOutcome.Cancelled;
        }
    }
    /// <summary>
    /// Executes the writeback in an already chosen mode, so a batch export asks once instead of
    /// once per drawing. Every export path must plan its destination first (see
    /// <see cref="BatchExportPlanner.CreateExportPlan"/>) and pass the planned backup path, otherwise
    /// AutoCAD writeback with "back up source before writeback" enabled fails inside the COM call.
    /// </summary>
    private async Task<(CadWriteResult result, bool usedAcadInterop, bool cancelled)> ExecuteWritebackWithMode(
        Views.ExportModeDialog.ExportMode mode, string sourceFilePath, string destFilePath,
        List<TextEntity> entitiesToWrite, string? plannedBackupPath = null)
    {
        if (mode == Views.ExportModeDialog.ExportMode.AutoCAD)
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var result = await Task.Run(async () =>
                await _autoCadInteropService.WritebackViaAutoCadAsync(
                    sourceFilePath, destFilePath, entitiesToWrite, TargetIsCjk, _config, plannedBackupPath, progress,
                    _exportCts!.Token), _exportCts!.Token);
            return (result, true, false);
        }
        else
        {
            var result = await Task.Run(() =>
                _dwgWriterService.WriteTranslations(
                    sourceFilePath, destFilePath, entitiesToWrite, TargetIsCjk, _exportCts!.Token,
                    new WritebackOptions
                    {
                        OverwriteExisting = _config.DuplicatePolicy == "overwrite",
                        BackupSource = _config.BackupSourceBeforeWrite,
                        BackupPath = plannedBackupPath
                    }),
                _exportCts!.Token);
            return (result, false, false);
        }
    }

    /// <summary>
    /// Handles export result: shows success/failure messages and consumes license.
    /// </summary>
    private void HandleExportResult(
        CadWriteResult result, bool usedAcadInterop, bool isDxfSource, string destFilePath,
        IReadOnlyCollection<TextEntity> attemptedEntities)
    {
        if (result.SuccessCount > 0)
        {
            // CAD runs in a separate process and therefore updates serialized copies.
            // When the plugin reports a fully successful batch, mirror that state in the UI.
            if (result.FailCount == 0 && result.SuccessCount >= attemptedEntities.Count)
            {
                foreach (var entity in attemptedEntities)
                    entity.Status = TranslationStatus.WritebackSuccess;
            }

            if (_config.LicensingEnabled)
                ConsumeLicenseForExport();
            string modeText = usedAcadInterop ? Strings.Get("ExportModeAutoCad") : Strings.Get("ExportModeOffline");
            string formatText = isDxfSource ? "DXF" : "DWG";
            StatusMessage = Strings.Get("StatusDwgExportComplete", formatText, modeText, result.SuccessCount, Path.GetFileName(destFilePath));
            DwgTranslator.App.Views.PromptDialog.Show(
                Strings.Get("MsgDwgExportSuccess", formatText, modeText, result.SuccessCount, result.FailCount, destFilePath.Replace('\\', '/')),
                Strings.Get("MsgTitleExportSuccess"),
                MessageBoxButton.OK,
                result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            ApplyFilter();
            UpdateStatistics();
        }
        else
        {
            var errorMsg = string.Join("\n", result.Errors.Take(5));
            string formatText = isDxfSource ? "DXF" : "DWG";
            StatusMessage = Strings.Get("StatusDwgExportFailed", formatText);
            DwgTranslator.App.Views.PromptDialog.Show(Strings.Get("MsgDwgExportError", formatText, errorMsg),
                Strings.Get("MsgTitleExportError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Excel I/O

    [RelayCommand]
    private async Task ExportExcelAsync()
    {
        if (IsProcessing || IsLoggingIn) return;
        if (Entities.Count == 0) { StatusMessage = Strings.Get("StatusNoExportData"); return; }

        var dialog = new SaveFileDialog
        {
            Filter = Strings.Get("FilterExcelFiles"),
            Title = Strings.Get("DialogTitleSaveExcel"),
            FileName = $"translations_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };
        if (dialog.ShowDialog() != true) return;

        await RunWithProgress(async () =>
        {
            await _excelService.ExportToExcelAsync(Entities.ToList(), dialog.FileName);
            StatusMessage = Strings.Get("StatusExcelExported", Entities.Count, Path.GetFileName(dialog.FileName));
        }, Strings.Get("OperationExportExcel"), "StatusExcelExportFailed", "ExcelExportError");
    }

    [RelayCommand]
    private async Task ImportExcelAsync()
    {
        if (IsProcessing || IsLoggingIn) return;

        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("FilterExcelAllFiles"),
            Title = Strings.Get("DialogTitleSelectExcel")
        };
        if (dialog.ShowDialog() != true) return;

        await ImportExcelFilesAsync(dialog.FileName).ConfigureAwait(true);
    }

    /// <summary>
    /// Imports a reviewed Excel translation table. Shared by the toolbar dialog and drag-and-drop.
    /// </summary>
    public async Task ImportExcelFilesAsync(string filePath)
    {
        if (IsProcessing || IsLoggingIn) return;
        if (Entities.Count == 0)
        {
            StatusMessage = "请先导入对应的 DWG / DXF 图纸，再导入校对后的 XLSX 译文表。";
            DwgTranslator.App.Services.ToastService.Warning(StatusMessage);
            return;
        }

        await RunWithProgress(async () =>
        {
            var importedEntities = await _excelService.ImportFromExcelAsync(filePath);
            int updatedCount = 0;
            foreach (var imported in importedEntities)
            {
                // Prefer handle + source-file match for multi-file safety; fall back to handle-only.
                TextEntity? existing = null;
                if (!string.IsNullOrWhiteSpace(imported.SourceFilePath))
                {
                    var src = NormalizeSourcePath(imported.SourceFilePath);
                    existing = Entities.FirstOrDefault(e =>
                        string.Equals(e.Handle, imported.Handle, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(e.SourceFilePath) &&
                        string.Equals(NormalizeSourcePath(e.SourceFilePath), src, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // Backward compatibility for old spreadsheets that genuinely lack SourceFilePath.
                    existing = Entities.FirstOrDefault(e =>
                        string.Equals(e.Handle, imported.Handle, StringComparison.OrdinalIgnoreCase));
                }

                if (existing != null)
                {
                    existing.TranslatedText = imported.TranslatedText;
                    existing.GlossaryHit = imported.GlossaryHit;
                    existing.Status = imported.Status;
                    existing.Notes = imported.Notes;
                    if (!string.IsNullOrWhiteSpace(imported.SourceFilePath) &&
                        string.IsNullOrWhiteSpace(existing.SourceFilePath))
                    {
                        existing.SourceFilePath = imported.SourceFilePath;
                    }
                    updatedCount++;
                }
            }
            ApplyFilter();
            UpdateStatistics();
            if (updatedCount == 0)
            {
                StatusMessage = "XLSX 已读取，但没有找到与当前图纸匹配的文本条目。请确认它来自当前图纸及相同版本。";
                DwgTranslator.App.Services.ToastService.Warning(StatusMessage);
            }
            else
            {
                StatusMessage = Strings.Get("StatusExcelImported", updatedCount);
            }
        }, Strings.Get("OperationImportExcel"), "StatusExcelImportFailed", "ExcelImportFormatError");
    }

    #endregion

    #region Progress Helper

    /// <summary>
    /// Runs an async action with standard progress state management.
    /// Sets IsProcessing/IsIndeterminate/OperationLabel, and resets on completion.
    /// </summary>
    private async Task RunWithProgress(Func<Task> action, string operationLabel,
        string? errorStatusKey = null, string? errorMsgKey = null)
    {
        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = operationLabel;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.Get("StatusExportCancelled2");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Operation failed: {Label}", operationLabel);
            StatusMessage = Strings.Get(errorStatusKey ?? "StatusCadExportFailed");
            DwgTranslator.App.Views.PromptDialog.Show(Strings.Get(errorMsgKey ?? "MsgCadExportError"), Strings.Get("MsgTitleError"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            IsIndeterminate = false;
            OperationLabel = string.Empty;
            IsCancellationRequested = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    #endregion
}

