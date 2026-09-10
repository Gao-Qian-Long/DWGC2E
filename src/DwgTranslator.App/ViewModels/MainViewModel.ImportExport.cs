using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using Microsoft.Extensions.DependencyInjection;
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
        if (IsProcessing) return;

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
    public static readonly string[] SupportedExcelExtensions = [".xlsx", ".xls"];

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
        if (IsProcessing) return;

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
            return;
        }

        if (cadFiles.Count > 0)
            await ImportCadFilesAsync(cadFiles).ConfigureAwait(true);

        // Excel translations are matched against the entities that were just imported, so
        // they always run after the CAD pass.
        if (excelFiles.Count > 0 && !IsProcessing)
            await ImportExcelFilesAsync(excelFiles[0]).ConfigureAwait(true);

        if (unsupported.Count > 0)
            Log.Warning("Dropped files ignored ({Count}): {Files}", unsupported.Count, string.Join("; ", unsupported));
    }

    /// <summary>
    /// Imports one or more DWG/DXF files. Shared by the toolbar dialog and drag-and-drop.
    /// </summary>
    public async Task ImportCadFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (IsProcessing) return;
        if (filePaths.Count == 0) return;

        // Resolve DXF reader before entering background thread
        var dxfReader = App.Services?.GetService<IDxfReaderService>();

        await RunWithProgress(async () =>
        {
            var importErrors = new List<string>();
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
                        importErrors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                }
                return result;
            });

            _lastSourceFilePath = filePaths[0];
            Entities.Clear();
            foreach (var entity in allEntities)
                Entities.Add(entity);

            ApplyFilter();
            UpdateStatistics();

            if (importErrors.Count > 0 && allEntities.Count > 0)
            {
                StatusMessage = Strings.Get("StatusImportPartialFail", allEntities.Count, importErrors.Count);
                Log.Warning("Import errors ({Count}): {Errors}", importErrors.Count, string.Join("; ", importErrors));
            }
            else if (importErrors.Count > 0)
            {
                StatusMessage = Strings.Get("StatusCadImportFailed");
                MessageBox.Show(Strings.Get("MsgCadImportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                StatusMessage = Strings.Get("StatusImported", allEntities.Count, filePaths.Count);
            }
        }, Strings.Get("OperationImporting"), "StatusCadImportFailed", "MsgCadImportError");
    }

    #endregion

    #region DWG/DXF Export

    [RelayCommand]
    private async Task ExportDwgAsync()
    {
        if (IsProcessing) return;

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

        var translatedEntities = Entities
            .Where(e => e.Status is TranslationStatus.Translated or TranslationStatus.Reviewed
                or TranslationStatus.WritebackSuccess or TranslationStatus.WritebackFailed)
            .ToList();

        var invalidTranslations = translatedEntities
            .Where(e => !TranslationQualityValidator.IsAcceptable(
                e.PlainText, e.TranslatedText, CurrentSourceLang, CurrentTargetLang))
            .ToList();
        if (invalidTranslations.Count > 0)
        {
            foreach (var entity in invalidTranslations)
                entity.Status = TranslationStatus.TranslationFailed;
            UpdateStatistics();
            ApplyFilter();
            StatusMessage = $"发现 {invalidTranslations.Count} 条未完整翻译内容，已阻止导出并标记为失败。";
            MessageBox.Show(
                $"检测到 {invalidTranslations.Count} 条仍包含原文的结果。\n\n程序已阻止这些内容写入图纸，请点击“重试失败”后再导出。",
                "翻译完整性检查", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (translatedEntities.Count == 0)
        {
            StatusMessage = Strings.Get("StatusNoTranslatedText");
            MessageBox.Show(Strings.Get("MsgNoTranslatedData"),
                Strings.Get("MsgTitleNoData"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Several drawings loaded at once: the export flow forces ONE source drawing and ONE
        // destination name, so a folder of drawings could only be written one dialog-pair at a
        // time and looked un-exportable. Write every imported drawing into one chosen folder
        // instead, one "<name>_translated" file each.
        var distinctSources = translatedEntities
            .Select(e => NormalizeSourcePath(e.SourceFilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctSources.Count > 1)
        {
            await ExportAllSourcesAsync(distinctSources!, translatedEntities);
            return;
        }

        // Multi-file safety: only write entities that belong to the chosen source file.
        var (sourceFilePath, destFilePath, isDxfSource) = PromptForExportFiles(translatedEntities);
        if (sourceFilePath == null) return;

        var sourceFull = Path.GetFullPath(sourceFilePath);
        var unfinished = Entities.Where(e =>
            string.Equals(NormalizeSourcePath(e.SourceFilePath), sourceFull, StringComparison.OrdinalIgnoreCase) &&
            e.Status is TranslationStatus.Pending or TranslationStatus.TranslationFailed or TranslationStatus.GlossaryMatched)
            .ToList();
        if (unfinished.Count > 0)
        {
            StatusMessage = $"当前图纸仍有 {unfinished.Count} 条未完成翻译，已停止导出。";
            MessageBox.Show(StatusMessage + "\n请完成待翻译项或重试失败项，再导出完整图纸。",
                "翻译完整性检查", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var knownSourceCount = translatedEntities
            .Select(e => NormalizeSourcePath(e.SourceFilePath))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var entitiesToWrite = translatedEntities
            .Where(e =>
                (string.IsNullOrWhiteSpace(e.SourceFilePath) && knownSourceCount <= 1) ||
                string.Equals(NormalizeSourcePath(e.SourceFilePath), sourceFull, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entitiesToWrite.Count == 0)
        {
            StatusMessage = Strings.Get("StatusNoTranslatedText");
            MessageBox.Show(
                $"No translated entities belong to the selected source file:\n{sourceFilePath}",
                Strings.Get("MsgTitleNoData"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (entitiesToWrite.Count < translatedEntities.Count)
        {
            Log.Information(
                "Multi-file export scoped to {Source}: writing {Write}/{Total} translated entities",
                Path.GetFileName(sourceFilePath), entitiesToWrite.Count, translatedEntities.Count);
        }

        IsExporting = true;
        try
        {
            await RunWithProgress(async () =>
            {
                IsCancellationRequested = false;
                _exportCts = new CancellationTokenSource();
                ProgressValue = 0;

                var (result, usedAcadInterop, cancelled) = await ExecuteWriteback(
                    sourceFilePath, destFilePath!, entitiesToWrite, isDxfSource);

                if (cancelled) return;
                ProgressValue = 100;
                HandleExportResult(result, usedAcadInterop, isDxfSource, destFilePath!, entitiesToWrite);
            }, Strings.Get("OperationExporting"), "StatusCadExportFailed", "MsgCadExportError");
        }
        finally
        {
            IsExporting = false;
        }
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
            MessageBox.Show(Strings.Get("MsgNoTranslatedData"),
                Strings.Get("MsgTitleNoData"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Title = $"选择导出文件夹（将导出 {targets.Count} 张图纸）",
            InitialDirectory = Path.GetDirectoryName(targets[0]) ?? string.Empty
        };
        if (folderDialog.ShowDialog() != true) return;
        var targetFolder = folderDialog.FolderName;

        var modeDialog = new Views.ExportModeDialog(_autoCadInteropService.IsAutoCADAvailable(_config))
        {
            Owner = Application.Current.MainWindow
        };
        if (modeDialog.ShowDialog() != true)
        {
            StatusMessage = Strings.Get("StatusExportCancelled");
            return;
        }
        var mode = modeDialog.SelectedMode;

        var written = new List<(string Path, int Count)>();
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
                        skipped.Add($"{name}（仍有 {unfinished} 条未完成翻译）");
                        Log.Warning("Batch export skipped {File}: {Count} unfinished entities", name, unfinished);
                        ProgressValue = (index + 1) * step;
                        continue;
                    }

                    var extension = Path.GetExtension(source);
                    var dest = Path.Combine(
                        targetFolder,
                        Path.GetFileNameWithoutExtension(source) + "_translated" + extension);
                    if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add($"{name}（输出文件名与源文件相同）");
                        ProgressValue = (index + 1) * step;
                        continue;
                    }

                    try
                    {
                        var (result, _, cancelled) = await ExecuteWritebackWithMode(mode, source, dest, mine);
                        if (cancelled) break;
                        if (result.SuccessCount > 0)
                        {
                            if (result.FailCount == 0 && result.SuccessCount >= mine.Count)
                                foreach (var entity in mine)
                                    entity.Status = TranslationStatus.WritebackSuccess;
                            written.Add((dest, result.SuccessCount));
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
                foreach (var (path, count) in written)
                    summary.AppendLine($"    ✓ {Path.GetFileName(path)}（{count} 条译文）");
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
                MessageBox.Show(summary.ToString(),
                    Strings.Get(written.Count > 0 ? "MsgTitleExportSuccess" : "MsgTitleExportError"),
                    MessageBoxButton.OK,
                    failed.Count > 0 || written.Count == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

                ApplyFilter();
                UpdateStatistics();
            }, Strings.Get("OperationExporting"), "StatusCadExportFailed", "MsgCadExportError");
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// Prompts user for source and destination files for DWG export.
    /// When multiple source drawings are loaded, forces an explicit source choice.
    /// Returns nulls if cancelled.
    /// </summary>
    private (string? sourcePath, string? destPath, bool isDxf) PromptForExportFiles(List<TextEntity>? candidates = null)
    {
        string? sourceFilePath = null;

        var knownSources = (candidates ?? Entities.ToList())
            .Select(e => e.SourceFilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (knownSources.Count == 1)
        {
            sourceFilePath = knownSources[0];
        }
        else if (knownSources.Count > 1)
        {
            var dialog = new OpenFileDialog
            {
                Filter = Strings.Get("FilterCadFiles"),
                Title = Strings.Get("DialogTitleSelectCadFile")
            };
            if (dialog.ShowDialog() == true) sourceFilePath = dialog.FileName;
            else return (null, null, false);
        }
        else if (!string.IsNullOrEmpty(_lastSourceFilePath) && File.Exists(_lastSourceFilePath))
        {
            sourceFilePath = _lastSourceFilePath;
        }
        else
        {
            var dialog = new OpenFileDialog
            {
                Filter = Strings.Get("FilterCadFiles"),
                Title = Strings.Get("DialogTitleSelectCadFile")
            };
            if (dialog.ShowDialog() == true) sourceFilePath = dialog.FileName;
        }

        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            StatusMessage = Strings.Get("StatusSelectCadFile");
            return (null, null, false);
        }

        var isDxfSource = Path.GetExtension(sourceFilePath).ToLowerInvariant() == ".dxf";
        var saveDialog = new SaveFileDialog
        {
            Filter = isDxfSource ? Strings.Get("FilterDxfFiles") : Strings.Get("FilterDwgFiles"),
            Title = Strings.Get("DialogTitleSaveDwg", isDxfSource ? "DXF" : "DWG"),
            FileName = Path.GetFileNameWithoutExtension(sourceFilePath) + "_translated" +
                       Path.GetExtension(sourceFilePath)
        };

        if (saveDialog.ShowDialog() != true)
            return (null, null, false);

        if (string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(saveDialog.FileName),
                StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "输出文件不能覆盖原始图纸，请选择新的文件名。";
            MessageBox.Show(StatusMessage, Strings.Get("MsgTitleConfigError"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return (null, null, false);
        }

        return (sourceFilePath, saveDialog.FileName, isDxfSource);
    }

    /// <summary>
    /// Executes the writeback via AutoCAD interop or offline mode.
    /// </summary>
    private async Task<(CadWriteResult result, bool usedAcadInterop, bool cancelled)> ExecuteWriteback(
        string sourceFilePath, string destFilePath,
        List<TextEntity> entitiesToWrite, bool isDxfSource)
    {
        var modeDialog = new Views.ExportModeDialog(
            _autoCadInteropService.IsAutoCADAvailable(_config))
        {
            Owner = Application.Current.MainWindow
        };

        if (modeDialog.ShowDialog() != true)
        {
            StatusMessage = Strings.Get("StatusExportCancelled");
            return (new CadWriteResult(), false, true);
        }

        return await ExecuteWritebackWithMode(
            modeDialog.SelectedMode, sourceFilePath, destFilePath, entitiesToWrite);
    }

    /// <summary>
    /// Executes the writeback in an already chosen mode, so a batch export asks once instead of
    /// once per drawing.
    /// </summary>
    private async Task<(CadWriteResult result, bool usedAcadInterop, bool cancelled)> ExecuteWritebackWithMode(
        Views.ExportModeDialog.ExportMode mode, string sourceFilePath, string destFilePath,
        List<TextEntity> entitiesToWrite)
    {
        if (mode == Views.ExportModeDialog.ExportMode.AutoCAD)
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var result = await Task.Run(async () =>
                await _autoCadInteropService.WritebackViaAutoCadAsync(
                    sourceFilePath, destFilePath, entitiesToWrite, TargetIsCjk, _config, progress,
                    _exportCts!.Token), _exportCts!.Token);
            return (result, true, false);
        }
        else
        {
            var result = await Task.Run(() =>
                _dwgWriterService.WriteTranslations(
                    sourceFilePath, destFilePath, entitiesToWrite, TargetIsCjk, _exportCts!.Token),
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
            MessageBox.Show(
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
            MessageBox.Show(Strings.Get("MsgDwgExportError", formatText, errorMsg),
                Strings.Get("MsgTitleExportError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Excel I/O

    [RelayCommand]
    private async Task ExportExcelAsync()
    {
        if (IsProcessing) return;
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
        if (IsProcessing) return;

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
        if (IsProcessing) return;

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
            StatusMessage = Strings.Get("StatusExcelImported", updatedCount);
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
            MessageBox.Show(Strings.Get(errorMsgKey ?? "MsgCadExportError"), Strings.Get("MsgTitleError"),
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
