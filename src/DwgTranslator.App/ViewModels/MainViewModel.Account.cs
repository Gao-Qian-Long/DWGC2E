using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.App.Services;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Collections.ObjectModel;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public enum AccountSessionState { SignedOut, SigningIn, Validating, SignedIn, Expired, Offline, ConfigurationError }

public partial class MainViewModel
{
    [ObservableProperty] private ProfileInfo? _onlineProfile;
    [ObservableProperty] private SubscriptionInfo? _onlineSubscription;
    [ObservableProperty] private UsageInfo? _onlineUsage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshAccountButtonText))]
    private bool _isAccountRefreshing;
    public ObservableCollection<DeviceInfo> OnlineDevices { get; } = new();
    [ObservableProperty] private string _loginName = string.Empty;
    [ObservableProperty] private string _accountFeedback = "可先浏览软件，使用云端翻译前请登录。";
    [ObservableProperty] private bool _isLoggingIn;
    [ObservableProperty] private AccountSessionState _accountState = AccountSessionState.SignedOut;
    private bool _sessionVerified;
    private bool _devicesSynced;
    private int _sessionVersion;
    private string? _loginReturnPage;
    private DateTime _lastMembershipRefreshUtc = DateTime.MinValue;
    private bool _isMembershipActivationRefreshRunning;
    public bool IsAccountLoggedIn => _sessionVerified;
    public string RefreshAccountButtonText => IsAccountRefreshing ? "同步中…" : "刷新权益";
    public string SavedSessionRecoveryButtonText => IsAccountRefreshing ? "正在验证…" : "重新验证会话";
    public bool HasSavedAccountSession => !string.IsNullOrWhiteSpace(AppConfig.DecryptApiKey(_config.AuthTokenEncrypted));
    public string AccountEntryText => IsAccountLoggedIn ? AccountDisplayNameText : "登录账号";
    public bool RequireAccount()
    {
        if (IsLoggingIn) { AccountFeedback = "账户正在切换，请稍后再执行操作。"; return false; }
        if (IsAccountLoggedIn) return true;
        _loginReturnPage = CurrentPage;
        LoginAccount();
        AccountFeedback = "此操作需要登录。登录后返回原页面，请再次确认操作。";
        return false;
    }
    private void NotifyAccount()
    {
        foreach (var name in new[] { nameof(IsAccountLoggedIn), nameof(AccountEntryText), nameof(AccountAvatarText), nameof(AccountDisplayNameText), nameof(AccountEmailText), nameof(OnlinePlanText), nameof(OnlineMembershipExpiryText), nameof(OnlineQuotaText), nameof(DeviceCountText), nameof(AccountSessionText), nameof(HasSavedAccountSession), nameof(TranslationServiceText) }) OnPropertyChanged(name);
    }
    public string AccountAvatarText
    {
        get
        {
            var name = (OnlineProfile?.DisplayName ?? "U").Trim();
            return name.Length == 0 ? "U" : name[..Math.Min(2, name.Length)].ToUpperInvariant();
        }
    }
    public string AccountDisplayNameText => string.IsNullOrWhiteSpace(OnlineProfile?.DisplayName) ? (IsAccountLoggedIn ? "已登录账号" : "未登录") : OnlineProfile.DisplayName;
    public string AccountEmailText => OnlineProfile?.Email ?? string.Empty;
    public string OnlinePlanText => string.IsNullOrWhiteSpace(OnlineSubscription?.PlanName) ? "未同步套餐" : OnlineSubscription.PlanName;
    public string MembershipTierColor => (OnlineSubscription?.PlanName ?? "").ToLowerInvariant() switch
    { "max" => "#A66B12", "pro" => "#974719", "go" => "#188568", _ => "#6E6B64" };
    public bool HasPaidTier => OnlineSubscription?.PlanName?.ToLowerInvariant() is "pro" or "max" or "go";
    partial void OnOnlineSubscriptionChanged(SubscriptionInfo? value)
    {
        OnPropertyChanged(nameof(MembershipTierColor)); OnPropertyChanged(nameof(HasPaidTier));
        OnPropertyChanged(nameof(OnlinePlanText)); OnPropertyChanged(nameof(OnlineMembershipExpiryText));
    }
    public string OnlineMembershipExpiryText => OnlineSubscription?.ExpiresAt is DateTime expiry
        ? $"会员到期：{expiry.ToLocalTime():yyyy-MM-dd HH:mm}" : HasPaidTier ? "到期时间未提供，以账号权益为准" : "免费套餐 · 无需续费";

    private DwgTranslator.App.Views.BillingWindow? _billingWindow;
    [RelayCommand]
    private void OpenMembershipCheckout()
    {
        if (!IsAccountLoggedIn || _apiClient is not IBillingClient billing) { AccountFeedback = "请先登录账户后购买会员；会员权益和额度以账户服务为准。"; return; }
        if (_billingWindow != null) { _billingWindow.Activate(); return; }
        var version = _sessionVersion;
        var window = new DwgTranslator.App.Views.BillingWindow(billing, AccountEmailText,
            () => version == _sessionVersion && IsAccountLoggedIn,
            ApplyBillingEntitlements) { Owner = System.Windows.Application.Current.MainWindow };
        _billingWindow = window;
        System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => { if (version != _sessionVersion || !IsAccountLoggedIn) window.Close(); };
        PropertyChanged += changed;
        window.Closed += (_, _) => { PropertyChanged -= changed; _billingWindow = null; };
        window.Show();
    }
    private void ApplyBillingEntitlements(BillingEntitlements snapshot)
    {
        OnlineSubscription = new SubscriptionInfo { PlanName=snapshot.Subscription.PlanName, StartsAt=snapshot.Subscription.StartsAt, ExpiresAt=snapshot.Subscription.ExpiresAt };
        OnlineUsage = new UsageInfo { MonthlyQuota=snapshot.Usage.MonthlyQuota, Used=snapshot.Usage.Used, ResetAt=snapshot.Usage.ResetAt };
        NotifyAccount();
    }
    public async Task RefreshMembershipOnActivationAsync()
    {
        if (!IsAccountLoggedIn || IsAccountRefreshing || _isMembershipActivationRefreshRunning || _apiClient is not IBillingClient billing) return;
        if (DateTime.UtcNow - _lastMembershipRefreshUtc < TimeSpan.FromMinutes(5)) return;

        _lastMembershipRefreshUtc = DateTime.UtcNow;
        var version = _sessionVersion;
        _isMembershipActivationRefreshRunning = true;
        try
        {
            var snapshot = await billing.GetBillingEntitlementsAsync();
            if (version == _sessionVersion && IsAccountLoggedIn) ApplyBillingEntitlements(snapshot);
        }
        catch (Exception ex) { Log.Debug("会员自动同步未完成：{Type}", ex.GetType().Name); }
        finally { _isMembershipActivationRefreshRunning = false; }
    }

    private static async Task<BillingEntitlements?> ReadOptionalBillingSnapshot(IBillingClient billing)
    {
        try { return await billing.GetBillingEntitlementsAsync(); }
        catch (ApiAuthenticationException) { throw; }
        catch (Exception ex) { Log.Debug("会员快照暂不可用：{Type}", ex.GetType().Name); return null; }
    }

    partial void OnOnlineUsageChanged(UsageInfo? value) => OnPropertyChanged(nameof(OnlineQuotaText));
    public string OnlineQuotaText => OnlineUsage == null ? "额度待同步" : $"剩余 {OnlineUsage.Remaining:N0} / {OnlineUsage.MonthlyQuota:N0}";
    public string DeviceCountText => !_devicesSynced ? "设备待同步" : $"已绑定 {OnlineDevices.Count} 台设备";

    [RelayCommand]
    private async Task RefreshAccountAsync()
    {
        if (IsAccountRefreshing)
        {
            AccountFeedback = "正在验证会话并同步账户，请稍候。";
            ToastService.Info(AccountFeedback);
            return;
        }
        if (!HasSavedAccountSession)
        {
            AccountFeedback = "当前没有可验证的登录会话，请输入账号和密码登录。";
            ToastService.Info(AccountFeedback);
            return;
        }
        if (!_apiClient.IsConfigured)
        {
            AccountState = AccountSessionState.ConfigurationError;
            AccountFeedback = "账户服务配置异常，请联系管理员修复安装配置。";
            ToastService.Warning(AccountFeedback);
            return;
        }
        var sessionVersion = _sessionVersion;
        IsAccountRefreshing = true;
        AccountState = AccountSessionState.Validating;
        AccountFeedback = "正在验证会话并同步账户…";
        try
        {
            var profileTask = _apiClient.GetProfileAsync();
            var billingTask = _apiClient is IBillingClient billing ? ReadOptionalBillingSnapshot(billing) : null;
            var subscriptionTask = billingTask == null ? _apiClient.GetSubscriptionAsync() : Task.FromResult<SubscriptionInfo?>(null);
            var usageTask = billingTask == null ? _apiClient.GetUsageAsync() : Task.FromResult<UsageInfo?>(null);
            var devicesTask = _apiClient.GetDevicesAsync();
            await Task.WhenAll(profileTask, subscriptionTask, usageTask, devicesTask, (Task?)billingTask ?? Task.CompletedTask).ConfigureAwait(true);
            if (sessionVersion != _sessionVersion) return;
            OnlineProfile = await profileTask;
            _sessionVerified = OnlineProfile != null || _sessionVerified;
            AccountState = OnlineProfile != null ? AccountSessionState.SignedIn : AccountSessionState.Offline;
            AccountFeedback = OnlineProfile == null ? "暂时无法同步账户，请检查网络后重试。" : "账户信息已同步。";
            if (OnlineProfile != null) _lastMembershipRefreshUtc = DateTime.UtcNow;
            if (billingTask != null) { var snapshot = await billingTask; if(snapshot != null) ApplyBillingEntitlements(snapshot); else AccountFeedback = OnlineProfile != null ? "已登录，会员与额度暂未同步，请稍后刷新；请勿重复付款。" : AccountFeedback; }
            else { OnlineSubscription = await subscriptionTask; OnlineUsage = await usageTask; }
            var devices = await devicesTask;
            _devicesSynced = devices != null;
            OnlineDevices.Clear();
            if (devices != null) foreach (var device in devices) OnlineDevices.Add(device);
            OnPropertyChanged(nameof(IsAccountLoggedIn));
            OnPropertyChanged(nameof(AccountAvatarText));
            OnPropertyChanged(nameof(AccountDisplayNameText));
            OnPropertyChanged(nameof(AccountEmailText));
            OnPropertyChanged(nameof(OnlinePlanText));
            OnPropertyChanged(nameof(OnlineQuotaText));
            OnPropertyChanged(nameof(DeviceCountText));
            if (AccountFeedback == "账户信息已同步。" && CurrentPage == PageAccount) ToastService.Success("账户信息已同步");
        }
        catch (ApiAuthenticationException ex)
        {
            if (sessionVersion != _sessionVersion) return;
            Log.Information("在线账户会话已失效：{ErrorCode}", ex.ErrorCode);
            var expiredCredential = _config.AuthTokenEncrypted;
            var retainedProofreading = HasUnsavedProofreading;
            var retainedEdits = retainedProofreading || HasUnsavedTerms || HasUnsavedSettings;
            if (retainedEdits)
            {
                // Revoke authentication without changing the owner of unsaved local edits.
                // Explicit login/logout still asks the user before replacing this workspace.
                try { SaveAccountSession("", _config.ActiveAccountId); }
                catch (Exception saveEx)
                {
                    ScheduleRejectedCredentialCleanup(expiredCredential);
                    Log.Warning(saveEx, "失效凭据无法从磁盘清除；已撤销内存会话并保留校对工作区");
                }
            }
            else
            {
                try { await SwitchAccountWorkspaceAsync("", ""); }
                catch (Exception saveEx)
                {
                    ScheduleRejectedCredentialCleanup(expiredCredential);
                    Log.Debug(saveEx, "清除失效会话失败；保留工作区并重试凭据清理");
                }
            }
            _sessionVerified = false;
            _devicesSynced = false;
            AccountState = AccountSessionState.Expired;
            AccountFeedback = retainedProofreading
                ? "登录已过期，未保存的校对仍保留在当前工作区。请先处理校对更改，再重新登录。"
                : retainedEdits ? "登录已过期，未保存的术语或设置仍保留。请先处理更改，再重新登录。"
                : "登录已过期，请重新登录。";
            NotifyAccount();
            OnlineProfile = null;
            OnlineSubscription = null;
            OnlineUsage = null;
            OnlineDevices.Clear();
            OnPropertyChanged(nameof(IsAccountLoggedIn));
            OnPropertyChanged(nameof(AccountAvatarText));
            OnPropertyChanged(nameof(AccountSessionText));
            OnPropertyChanged(nameof(IsAccountLoggedIn));
            OnPropertyChanged(nameof(AccountAvatarText));
            OnPropertyChanged(nameof(AccountDisplayNameText));
            OnPropertyChanged(nameof(AccountEmailText));
            OnPropertyChanged(nameof(OnlinePlanText));
            OnPropertyChanged(nameof(OnlineQuotaText));
            OnPropertyChanged(nameof(DeviceCountText));
            NotifyAccount();
            ToastService.Warning("登录已过期，请重新登录。");
        }
        catch (Exception ex) { if (sessionVersion != _sessionVersion) return; AccountState = AccountSessionState.Offline; AccountFeedback = "暂时无法连接服务，请检查网络后重试。"; Log.Warning(ex, "刷新在线账户信息失败"); ToastService.Warning(AccountFeedback); }
        finally { IsAccountRefreshing = false; NotifyAccount(); }
    }
    [RelayCommand]
    private async Task RevokeDeviceAsync(DeviceInfo? device)
    {
        if (device == null || string.IsNullOrWhiteSpace(device.DeviceId) || IsAccountRefreshing) return;
        if (device.IsCurrent)
        {
            ToastService.Warning("当前设备不能在本机撤销，请在其他设备或网页端操作。");
            return;
        }
        var answer = DwgTranslator.App.Views.PromptDialog.Show($"确定要移除设备“{device.DeviceName}”吗？", "移除设备", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            if (!await _apiClient.RevokeDeviceAsync(device.DeviceId))
            {
                AccountFeedback = "设备移除失败，请稍后重试。"; ToastService.Error(AccountFeedback);
                return;
            }
            OnlineDevices.Remove(device);
            OnPropertyChanged(nameof(DeviceCountText));
            ToastService.Success("设备已移除。");
        }
        catch (Exception ex) { Log.Warning(ex, "移除设备失败"); AccountFeedback = "设备移除失败，请稍后重试。"; ToastService.Error(AccountFeedback); }
    }

    public string AccountSessionText => string.IsNullOrEmpty(AppConfig.DecryptApiKey(_config.AuthTokenEncrypted))
        ? "尚未登录" : "已保存登录会话";

    [RelayCommand]
    private void LoginAccount()
    {
        CurrentPage = PageAccount;
        if (!_apiClient.IsConfigured) { AccountState = AccountSessionState.ConfigurationError; AccountFeedback = "服务配置异常，请联系管理员修复安装配置后重试。"; }
    }

    public async Task SubmitLoginAsync(string password)
    {
        if (IsLoggingIn || IsAccountRefreshing || IsExporting || IsGlossaryLoading || IsProcessing) return;
        if (!ConfirmLeaveProofreading() || !ConfirmLeaveGlossary()) return;
        if (IsProcessing) { AccountFeedback = "任务正在执行，请完成或停止任务后再切换账号。"; return; }
        if (!_apiClient.IsConfigured) { AccountState = AccountSessionState.ConfigurationError; AccountFeedback = "服务配置异常，请联系管理员修复安装配置后重试。"; return; }
        if (string.IsNullOrWhiteSpace(LoginName) || string.IsNullOrEmpty(password)) { AccountFeedback = "请输入账号和密码。"; return; }
        IsLoggingIn = true;
        AccountState = AccountSessionState.SigningIn;
        AccountFeedback = "正在登录…";
        try
        {
            if (HasSavedAccountSession && _sessionVerified)
            {
                AccountFeedback = "请先退出当前账号，再登录其他账号。";
                AccountState = _sessionVerified ? AccountSessionState.SignedIn : AccountSessionState.Offline;
                return;
            }
            _taskManager.EnsureAccountStoreSaved();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var result = await _apiClient.LoginAsync(LoginName.Trim(), password, cts.Token);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Token)) { AccountState = AccountSessionState.SignedOut; AccountFeedback = result.Message ?? "登录失败，请检查账号、密码后重试。"; return; }
            if (result.ExpiresAt.HasValue && result.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow) { AccountState = AccountSessionState.Expired; AccountFeedback = "登录会话已过期，请重试。"; return; }
            await SwitchAccountWorkspaceAsync(result.Token, result.UserId ?? LoginName.Trim().ToLowerInvariant());
            _sessionVerified = true;
            AccountState = AccountSessionState.SignedIn;
            AccountFeedback = "登录成功。";
            NotifyAccount();
            await RefreshAccountAsync();
            if (IsAccountLoggedIn && _loginReturnPage != null) { var page = _loginReturnPage; _loginReturnPage = null; CurrentPage = page; }
        }
        catch (OperationCanceledException) { AccountState = AccountSessionState.Offline; AccountFeedback = "登录超时，请检查网络后重试。"; }
        catch (IOException) { AccountFeedback = "任务或登录会话无法保存，已停止账号切换。请检查磁盘空间和目录权限后重试。"; }
        catch (Exception ex) { AccountState = AccountSessionState.Offline; Log.Warning(ex, "Login failed"); AccountFeedback = "暂时无法登录，请检查网络或稍后重试。"; }
        finally { IsLoggingIn = false; if (AccountState == AccountSessionState.SigningIn) AccountState = AccountSessionState.SignedOut; }
    }

    [RelayCommand]
    private async Task LogoutAccountAsync()
    {
        if (IsLoggingIn || IsAccountRefreshing || IsExporting || IsGlossaryLoading || IsProcessing) return;
        if (!ConfirmLeaveProofreading() || !ConfirmLeaveGlossary()) return;
        if (IsProcessing) { AccountFeedback = "任务正在执行，完成或停止任务后可以退出登录。"; return; }
        IsLoggingIn = true;
        var logoutCredential = _config.AuthTokenEncrypted;
        var remoteLogoutConfirmed = false;
        var remoteLogoutAttempted = false;
        try
        {
            _taskManager.EnsureAccountStoreSaved();
            if (_apiClient is IAccountSessionClient sessions && HasSavedAccountSession)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                remoteLogoutAttempted = true;
                if (!await sessions.LogoutAsync(cts.Token))
                {
                    AccountFeedback = "服务端退出尚未确认，请检查网络后重试。";
                    return;
                }
                remoteLogoutConfirmed = true;
                // Revocation cannot be rolled back by a subsequent local persistence failure.
                ClearVerifiedAccountSession();
            }
            await SwitchAccountWorkspaceAsync("", "");
            if (!remoteLogoutConfirmed) ClearVerifiedAccountSession();
            AccountFeedback = "已退出登录；APP 绑定仍保留，可在网页解除绑定释放名额。";
            NotifyAccount();
            ToastService.Success("已清除本机登录会话。");
        }
        catch (Exception ex)
        {
            if (remoteLogoutConfirmed) ScheduleRejectedCredentialCleanup(logoutCredential);
            Log.Warning(ex, "退出登录未能完成本地或远端清理");
            AccountFeedback = remoteLogoutConfirmed
                ? "云端已退出登录，本机登录权限已撤销；本地清理未完成，任务已保留。请检查磁盘空间和目录权限后重试退出。"
                : !remoteLogoutAttempted && ex is IOException
                    ? "任务或登录会话无法保存，已保留当前工作区。请检查磁盘空间和目录权限后重试。"
                    : "服务端退出尚未确认，请检查网络和配置目录后重试。";
            ToastService.Error(AccountFeedback);
        }
        finally { IsLoggingIn = false; }
    }

    private void ClearVerifiedAccountSession()
    {
        _sessionVersion++;
        _sessionVerified = false;
        _devicesSynced = false;
        _lastMembershipRefreshUtc = DateTime.MinValue;
        AccountState = AccountSessionState.SignedOut;
        OnlineProfile = null;
        OnlineSubscription = null;
        OnlineUsage = null;
        OnlineDevices.Clear();
        NotifyAccount();
    }
    private string AccountDataDirectory => DwgTranslator.Core.Services.AccountWorkspace.DirectoryFor(App.AppDataDir, _config.ActiveAccountId);
    private async Task SwitchAccountWorkspaceAsync(string token, string accountId)
    {
        if (_taskManager.IsRunning || IsExporting) throw new InvalidOperationException("请先停止当前任务。");
        // Persist the outgoing account's resolved default before changing ownership.
        _config.AccountOutputDirectories ??= new();
        _config.AccountOutputDirectories[string.IsNullOrWhiteSpace(_config.ActiveAccountId) ? "guest" : _config.ActiveAccountId] = _config.ExportDirectory;
        DwgTranslator.Core.Services.SettingsStore.Update(_settingsPath!, c => {
            c.AccountOutputDirectories ??= new();
            c.AccountOutputDirectories[string.IsNullOrWhiteSpace(_config.ActiveAccountId) ? "guest" : _config.ActiveAccountId] = _config.ExportDirectory;
        });
        var destination = DwgTranslator.Core.Services.AccountWorkspace.DirectoryFor(App.AppDataDir, accountId);
        Directory.CreateDirectory(destination);
        // SwitchAccountStore flushes the outgoing store and commits the session itself; calling
        // these through ITaskManager keeps the fail-closed gate from being silently skipped.
        _taskManager.SwitchAccountStore(
            new DwgTranslator.Core.Tasks.JsonTaskStore(Path.Combine(destination, "tasks.json")),
            () => SaveAccountSession(token, accountId));
        RefreshTaskRecoveryNotice();
        if (_consistencyService is DwgTranslator.Core.Translation.TranslationConsistencyService cache)
            cache.SwitchAccountFile(Path.Combine(AccountDataDirectory, "translation_cache.json"));
        DrawingFiles.Clear(); Entities.Clear(); FilteredEntities.Clear(); _entityIndex = null;
        SelectedDrawingFile = null; SelectedFilePath = ""; HasDrawingFiles = false; HasMultipleDrawingFiles = false;
        TotalCount = TranslatedCount = FailedCount = GlossaryHitCount = CacheHitCount = 0;
        _config.ExportDirectory = DwgTranslator.Core.Services.AccountWorkspace.OutputDirectoryFor(_config, App.AppDataDir);
        _settingsDraft = null;
        OnPropertyChanged(nameof(SettingsDraft));
        if (!string.IsNullOrWhiteSpace(_config.ExportDirectory))
            Directory.CreateDirectory(_config.ExportDirectory);
        await RefreshGlossaryDataAsync();
        foreach (var task in _taskManager.Tasks) OnTaskUpdated(task);
        await RestoreSavedProofreadingAsync();
    }

    private void SaveAccountSession(string token, string? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
            throw new InvalidOperationException("Settings path is not initialized.");
        var encrypted = AppConfig.EncryptApiKey(token);
        DwgTranslator.Core.Services.SettingsStore.Update(_settingsPath, c => { c.AuthTokenEncrypted = encrypted; c.ActiveAccountId = accountId ?? ""; });
        StopRejectedCredentialCleanup();
        _config.ActiveAccountId = accountId ?? "";
        _config.AuthTokenEncrypted = encrypted;
        _sessionVersion++;
    }
}

