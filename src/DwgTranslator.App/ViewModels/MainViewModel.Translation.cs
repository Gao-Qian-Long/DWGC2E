using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using System.IO;
using System.Net.Http;
using System.Windows;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    #region Translation

    [RelayCommand]
    private async Task TranslateAsync()
    {
        if (IsProcessing) return;

        if (!_licenseService.CanExecuteOperation())
        {
            StatusMessage = Strings.Get("StatusLicenseRequired");
            PromptForActivation();
            return;
        }

        var entitiesToTranslate = Entities
            .Where(e => !e.IsXref && e.Status == TranslationStatus.Pending)
            .ToList();

        if (entitiesToTranslate.Count == 0)
        {
            StatusMessage = Strings.Get("StatusNoPendingText");
            return;
        }

        if (string.IsNullOrEmpty(_config.DeepSeekApiKey))
        {
            StatusMessage = Strings.Get("StatusConfigureApiKey");
            MessageBox.Show(Strings.Get("MsgNoApiKey"), Strings.Get("MsgTitleConfigError"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsProcessing = true;
        OperationLabel = Strings.Get("OperationTranslating");
        IsCancellationRequested = false;
        _cts = new CancellationTokenSource();
        TranslatedCount = 0;
        FailedCount = 0;
        CacheHitCount = 0;
        ProgressValue = 0;
        var totalCount = entitiesToTranslate.Count;
        var completedCount = 0;

        StatusMessage = Strings.Get("StatusTranslating", 0, totalCount);

        try
        {
            var systemPrompt = LoadSystemPrompt();
            EnsureDeepSeekClient();

            var progress = new Progress<TranslationPair>(pair =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var entity = Entities.FirstOrDefault(e => e.Handle == pair.Handle);
                    if (entity == null)
                    {
                        entity = Entities.FirstOrDefault(e =>
                            string.Equals(e.Handle, pair.Handle, StringComparison.OrdinalIgnoreCase));
                    }
                    if (entity == null) return;

                    entity.TranslatedText = pair.TranslatedText;
                    entity.GlossaryHit = pair.GlossaryHit;
                    entity.Status = pair.Status;

                    Interlocked.Increment(ref completedCount);

                    if (pair.Status == TranslationStatus.Translated)
                    {
                        TranslatedCount++;
                        if (pair.GlossaryHit) GlossaryHitCount++;
                    }
                    else if (pair.Status == TranslationStatus.TranslationFailed)
                    {
                        FailedCount++;
                    }

                    CacheHitCount = _consistencyService.CacheSize;
                    ProgressValue = (double)completedCount / totalCount * 100;
                    StatusMessage = Strings.Get("StatusTranslating", completedCount, totalCount);
                });
            });

            await Task.Run(async () =>
            {
                var translationService = new TranslationService(
                    _glossaryService, _formatCodeParser, _deepSeekClient!, systemPrompt,
                    _config.BatchSize, _config.MaxRetryCount, _consistencyService, maxConcurrency: 5);

                await translationService.TranslateBatchWithProgressAsync(
                    entitiesToTranslate, CurrentSourceLang, CurrentTargetLang, progress, _cts.Token);
            }, _cts.Token);

            ApplyFilter();
            UpdateStatistics();
            StatusMessage = Strings.Get("StatusTranslateComplete", TranslatedCount, FailedCount, LanguageDirection);
        }
        catch (TaskCanceledException)
        {
            StatusMessage = Strings.Get("StatusTranslateTimeout");
            MessageBox.Show(Strings.Get("MsgTranslateTimeout"), Strings.Get("MsgTitleTimeout"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "Translation API error");
            StatusMessage = Strings.Get("StatusApiFailed");
            MessageBox.Show(Strings.Get("MsgApiError"), Strings.Get("MsgTitleApiError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.Get("StatusTranslateCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Translation error");
            StatusMessage = Strings.Get("StatusTranslateFailed");
            MessageBox.Show(Strings.Get("MsgTranslateError"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsProcessing = false;
            IsIndeterminate = false;
            OperationLabel = string.Empty;
            IsCancellationRequested = false;

            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                StatusMessage = Strings.Get("StatusTranslateComplete", TranslatedCount, FailedCount, LanguageDirection);
                UpdateStatistics();
                ApplyFilter();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    [RelayCommand]
    private void CancelTranslate()
    {
        IsCancellationRequested = true;
        _cts?.Cancel();
        StatusMessage = Strings.Get("StatusStoppingTranslation");
    }

    [RelayCommand]
    private void CancelExport()
    {
        IsCancellationRequested = true;
        _exportCts?.Cancel();
        StatusMessage = Strings.Get("StatusCancellingExport");
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        if (IsProcessing) return;

        var failedEntities = Entities.Where(e => e.Status == TranslationStatus.TranslationFailed).ToList();
        if (failedEntities.Count == 0) { StatusMessage = Strings.Get("StatusNoFailedRetry"); return; }

        foreach (var e in failedEntities) { e.Status = TranslationStatus.Pending; e.TranslatedText = string.Empty; }
        UpdateStatistics();
        await TranslateAsync();
    }

    #endregion

    #region Internal Helpers

    private string LoadSystemPrompt()
    {
        var appDataPath = Path.Combine(App.AppDataDir, "prompts", "deepl_context.txt");
        var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "deepl_context.txt");
        var promptPath = File.Exists(appDataPath) ? appDataPath : bundledPath;
        if (File.Exists(promptPath)) return File.ReadAllText(promptPath);
        return "You are a professional engineering translator. Output ONLY the translated text, nothing else.";
    }

    private void EnsureDeepSeekClient()
    {
        if (_deepSeekClient != null) return;

        if (string.IsNullOrWhiteSpace(_config.DeepSeekBaseUrl))
            throw new InvalidOperationException(Strings.Get("SettingsTestEnterKey"));

        if (string.IsNullOrWhiteSpace(_config.DeepSeekApiKey))
            throw new InvalidOperationException(Strings.Get("SettingsTestEnterKey"));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.DeepSeekBaseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.DeepSeekApiKey}");
        _deepSeekClient = new DeepSeekClient(_httpClient, _config.DeepSeekModel);
    }

    #endregion
}
