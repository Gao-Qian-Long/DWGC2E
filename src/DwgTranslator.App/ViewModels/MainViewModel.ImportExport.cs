using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.IO;
using System.Linq;
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

        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = Strings.Get("OperationImporting");
        StatusMessage = Strings.Get("StatusReadingCad", filePaths.Length);

        try
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
                        List<TextEntity> entities;
                        if (extension == ".dxf")
                        {
                            var dxfReader = App.Services?.GetService<IDxfReaderService>();
                            entities = dxfReader?.ExtractFromFile(path) ?? new List<TextEntity>();
                        }
                        else
                        {
                            entities = _dwgReaderService.ExtractFromFile(path);
                        }
                        foreach (var e in entities)
                            e.Notes = $"Source: {Path.GetFileNameWithoutExtension(path)}";
                        result.AddRange(entities);
                    }
                    catch (Exception ex)
                    {
                        importErrors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"Failed to read {path}: {ex.Message}");
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
            else if (importErrors.Count > 0 && allEntities.Count == 0)
            {
                StatusMessage = Strings.Get("StatusCadImportFailed");
                MessageBox.Show(Strings.Get("MsgCadImportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                StatusMessage = Strings.Get("StatusImported", allEntities.Count, filePaths.Length);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CAD import failed");
            StatusMessage = Strings.Get("StatusCadImportFailed");
            MessageBox.Show(Strings.Get("MsgCadImportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            IsIndeterminate = false;
            OperationLabel = string.Empty;
        }
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
        { StatusMessage = Strings.Get("StatusSelectCadFile"); return; }

        var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        var isDxfSource = sourceExtension == ".dxf";

        var dialog2 = new SaveFileDialog
        {
            Filter = isDxfSource ? Strings.Get("FilterDxfFiles") : Strings.Get("FilterDwgFiles"),
            Title = Strings.Get("DialogTitleSaveDwg", isDxfSource ? "DXF" : "DWG"),
            FileName = Path.GetFileNameWithoutExtension(sourceFilePath) + "_translated" + sourceExtension
        };
        if (dialog2.ShowDialog() != true) return;

        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = Strings.Get("OperationExporting");
        IsCancellationRequested = false;
        _exportCts = new CancellationTokenSource();
        StatusMessage = Strings.Get("StatusExportingDwg");
        ProgressValue = 0;

        try
        {
            CadWriteResult result;
            bool usedAcadInterop = false;

            var modeDialog = new Views.ExportModeDialog(_autoCadInteropService.IsAutoCADAvailable(_config))
            {
                Owner = Application.Current.MainWindow
            };

            if (modeDialog.ShowDialog() != true)
            {
                StatusMessage = Strings.Get("StatusExportCancelled");
                return;
            }

            if (modeDialog.SelectedMode == Views.ExportModeDialog.ExportMode.AutoCAD)
            {
                usedAcadInterop = true;

                MessageBox.Show(
                    Strings.Get("MsgAutoCadSecurity"),
                    Strings.Get("MsgTitleAutoCadSecurity"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                var writebackProgress = new Progress<string>(msg => StatusMessage = msg);
                result = await Task.Run(async () =>
                    await _autoCadInteropService.WritebackViaAutoCadAsync(sourceFilePath, dialog2.FileName, entitiesToWrite, IsCnToEn, _config, writebackProgress));
            }
            else
            {
                result = await Task.Run(() =>
                    _dwgWriterService.WriteTranslations(sourceFilePath, dialog2.FileName, entitiesToWrite, IsCnToEn, _exportCts!.Token), _exportCts.Token);
            }

            ProgressValue = 100;

            if (result.SuccessCount > 0)
            {
                ConsumeLicenseForExport();

                string modeText = usedAcadInterop ? Strings.Get("ExportModeAutoCad") : Strings.Get("ExportModeOffline");
                string formatText = isDxfSource ? "DXF" : "DWG";
                StatusMessage = Strings.Get("StatusDwgExportComplete", formatText, modeText, result.SuccessCount, Path.GetFileName(dialog2.FileName));
                MessageBox.Show(
                    Strings.Get("MsgDwgExportSuccess", formatText, modeText, result.SuccessCount, result.FailCount, dialog2.FileName),
                    Strings.Get("MsgTitleExportSuccess"),
                    MessageBoxButton.OK,
                    result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            else
            {
                var errorMsg = string.Join("\n", result.Errors.Take(5));
                string formatText = isDxfSource ? "DXF" : "DWG";
                StatusMessage = Strings.Get("StatusDwgExportFailed", formatText);
                MessageBox.Show(Strings.Get("MsgDwgExportError", formatText, errorMsg), Strings.Get("MsgTitleExportError"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.Get("StatusExportCancelled2");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CAD export failed");
            StatusMessage = Strings.Get("StatusCadExportFailed");
            MessageBox.Show(Strings.Get("MsgCadExportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
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

        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = Strings.Get("OperationExportExcel");
        StatusMessage = Strings.Get("StatusExportingExcel");
        try
        {
            await _excelService.ExportToExcelAsync(Entities.ToList(), dialog.FileName);
            StatusMessage = Strings.Get("StatusExcelExported", Entities.Count, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel export failed");
            StatusMessage = Strings.Get("StatusExcelExportFailed");
            MessageBox.Show(Strings.Get("ExcelExportError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsProcessing = false; IsIndeterminate = false; OperationLabel = string.Empty; }
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

        IsProcessing = true;
        IsIndeterminate = true;
        OperationLabel = Strings.Get("OperationImportExcel");
        StatusMessage = Strings.Get("StatusImportingExcel");
        try
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel import failed");
            StatusMessage = Strings.Get("StatusExcelImportFailed");
            MessageBox.Show(Strings.Get("ExcelImportFormatError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsProcessing = false; IsIndeterminate = false; OperationLabel = string.Empty; }
    }

    #endregion
}
