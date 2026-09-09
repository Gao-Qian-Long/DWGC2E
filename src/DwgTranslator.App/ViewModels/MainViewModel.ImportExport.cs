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

        var filePaths = dialog.FileNames;
        if (filePaths.Length == 0) return;

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
                StatusMessage = Strings.Get("StatusImported", allEntities.Count, filePaths.Length);
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

        if (modeDialog.SelectedMode == Views.ExportModeDialog.ExportMode.AutoCAD)
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var result = await Task.Run(async () =>
                await _autoCadInteropService.WritebackViaAutoCadAsync(
                    sourceFilePath, destFilePath, entitiesToWrite, IsCnToEn, _config, progress,
                    _exportCts!.Token), _exportCts!.Token);
            return (result, true, false);
        }
        else
        {
            var result = await Task.Run(() =>
                _dwgWriterService.WriteTranslations(
                    sourceFilePath, destFilePath, entitiesToWrite, IsCnToEn, _exportCts!.Token),
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

        await RunWithProgress(async () =>
        {
            var importedEntities = await _excelService.ImportFromExcelAsync(dialog.FileName);
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
