using System.Windows.Threading;
using DwgTranslator.Core.Services;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    private DispatcherTimer? _sessionCleanupTimer;
    private string? _pendingRejectedCredential;

    private void OnAuthenticationRejected(DwgTranslator.Core.Api.ApiAuthenticationException failure)
    {
        Log.Information("Worker 已拒绝当前会话：{ErrorCode}", failure.ErrorCode);
        OnUiThread(() =>
        {
            if (!HasSavedAccountSession) return;
            // Reuse the established expiry path so unsaved proofreading, terms and settings survive.
            _ = SafeRefreshAccountAsync(userInitiated: false); // 自动同步：后台会话被拒后的清理重试，不弹成功 toast（§A3 补完 t7）
        });
    }

    private void ScheduleRejectedCredentialCleanup(string encryptedToken)
    {
        _config.AuthTokenEncrypted = string.Empty;
        _sessionVersion++;
        StopRejectedCredentialCleanup();
        if (string.IsNullOrEmpty(encryptedToken)) return;
        _pendingRejectedCredential = encryptedToken;
        if (TryCleanupRejectedCredential()) return;
        _sessionCleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _sessionCleanupTimer.Tick += RetryRejectedCredentialCleanup;
        _sessionCleanupTimer.Start();
    }

    private void RetryRejectedCredentialCleanup(object? sender, EventArgs e) => TryCleanupRejectedCredential();

    private bool TryCleanupRejectedCredential()
    {
        if (_pendingRejectedCredential == null) return true;
        try
        {
            if (string.IsNullOrWhiteSpace(_settingsPath))
                throw new InvalidOperationException("Settings path is not initialized.");
            // A changed credential is deliberately left alone, including an external new login.
            SettingsStore.ClearAuthenticationIfMatches(_settingsPath, _pendingRejectedCredential);
            StopRejectedCredentialCleanup();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("失效凭据清理暂未完成，将在本进程内重试：{ErrorType}", ex.GetType().Name);
            return false;
        }
    }

    private void StopRejectedCredentialCleanup()
    {
        if (_sessionCleanupTimer != null)
        {
            _sessionCleanupTimer.Stop();
            _sessionCleanupTimer.Tick -= RetryRejectedCredentialCleanup;
            _sessionCleanupTimer = null;
        }
        _pendingRejectedCredential = null;
    }
}
