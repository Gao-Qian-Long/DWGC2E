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

                        foreach (var e in entities)
                            e.Notes = $"Source: {Path.GetFileNameWithoutExtension(path)}";
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

        if (!_licenseService.CanExecuteOperation())
        {
            StatusMessage = Strings.Get("StatusLicenseRequired");
            PromptForActivation();
            return;
        }

        var entitiesToWrite = Entities
            .Where(e => e.Status == TranslationStatus.Translated || e.Status == TranslationStatus.Reviewed)
            .ToList();

        if (entitiesToWrite.Count == 0)
        {
            StatusMessage = Strings.Get("StatusNoTranslatedText");
            MessageBox.Show(Strings.Get("MsgNoTranslatedData"),
                Strings.Get("MsgTitleNoData"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (sourceFilePath, destFilePath, isDxfSource) = PromptForExportFiles();
        if (sourceFilePath == null) return;

        await RunWithProgress(async () =>
        {
            IsCancellationRequested = false;
            _exportCts = new CancellationTokenSource();
            ProgressValue = 0;

            var (result, usedAcadInterop) = await ExecuteWriteback(
                sourceFilePath, destFilePath!, entitiesToWrite, isDxfSource);

            ProgressValue = 100;
            HandleExportResult(result, usedAcadInterop, isDxfSource, destFilePath!);
        }, Strings.Get("OperationExporting"), "StatusCadExportFailed", "MsgCadExportError");
    }

    /// <summary>
    /// Prompts user for source and destination files for DWG export.
    /// Returns nulls if cancelled.
    /// </summary>
    private (string? sourcePath, string? destPath, bool isDxf) PromptForExportFiles()
    {
        string? sourceFilePath = null;
        if (!string.IsNullOrEmpty(_lastSourceFilePath) && File.Exists(_lastSourceFilePath))
            sourceFilePath = _lastSourceFilePath;
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

        return saveDialog.ShowDialog() == true
            ? (sourceFilePath, saveDialog.FileName, isDxfSource)
            : (null, null, false);
    }

    /// <summary>
    /// Executes the writeback via AutoCAD interop or offline mode.
    /// </summary>
    private async Task<(CadWriteResult result, bool usedAcadInterop)> ExecuteWriteback(
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
            return (new CadWriteResult(), false);
        }

        if (modeDialog.SelectedMode == Views.ExportModeDialog.ExportMode.AutoCAD)
        {
            MessageBox.Show(
                Strings.Get("MsgAutoCadSecurity"),
                Strings.Get("MsgTitleAutoCadSecurity"),
                MessageBoxButton.OK, MessageBoxImage.Information);

            var progress = new Progress<string>(msg => StatusMessage = msg);
            var result = await Task.Run(async () =>
                await _autoCadInteropService.WritebackViaAutoCadAsync(
                    sourceFilePath, destFilePath, entitiesToWrite, IsCnToEn, _config, progress));
            return (result, true);
        }
        else
        {
            var result = await Task.Run(() =>
                _dwgWriterService.WriteTranslations(
                    sourceFilePath, destFilePath, entitiesToWrite, IsCnToEn, _exportCts!.Token),
                _exportCts!.Token);
            return (result, false);
        }
    }

    /// <summary>
    /// Handles export result: shows success/failure messages and consumes license.
    /// </summary>
    private void HandleExportResult(
        CadWriteResult result, bool usedAcadInterop, bool isDxfSource, string destFilePath)
    {
        if (result.SuccessCount > 0)
        {
            ConsumeLicenseForExport();
            string modeText = usedAcadInterop ? Strings.Get("ExportModeAutoCad") : Strings.Get("ExportModeOffline");
            string formatText = isDxfSource ? "DXF" : "DWG";
            StatusMessage = Strings.Get("StatusDwgExportComplete", formatText, modeText, result.SuccessCount, Path.GetFileName(destFilePath));
            MessageBox.Show(
                Strings.Get("MsgDwgExportSuccess", formatText, modeText, result.SuccessCount, result.FailCount, destFilePath.Replace('\\', '/')),
                Strings.Get("MsgTitleExportSuccess"),
                MessageBoxButton.OK,
                result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
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
                var existing = Entities.FirstOrDefault(e => e.Handle == imported.Handle);
                if (existing != null)
                {
                    existing.TranslatedText = imported.TranslatedText;
                    existing.GlossaryHit = imported.GlossaryHit;
                    existing.Status = imported.Status;
                    existing.Notes = imported.Notes;
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
