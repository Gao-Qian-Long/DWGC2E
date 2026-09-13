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
    [ObservableProperty] private bool _isAccountRefreshing;
    public ObservableCollection<DeviceInfo> OnlineDevices { get; } = new();
    [ObservableProperty] private string _loginName = string.Empty;
    [ObservableProperty] private string _accountFeedback = "可先浏览软件，使用云端翻译前请登录。";
    [ObservableProperty] private bool _isLoggingIn;
    [ObservableProperty] private AccountSessionState _accountState = AccountSessionState.SignedOut;
    private bool _sessionVerified;
    private bool _devicesSynced;
    private int _sessionVersion;
    private string? _loginReturnPage;
    public bool IsAccountLoggedIn => _sessionVerified;
    public bool HasSavedAccountSession => !string.IsNullOrWhiteSpace(AppConfig.DecryptApiKey(_config.AuthTokenEncrypted));
    public string AccountEntryText => IsAccountLoggedIn ? AccountDisplayNameText : "登录账号";
    public bool RequireAccount()
    {
        if (IsAccountLoggedIn) return true;
        _loginReturnPage = CurrentPage;
        LoginAccount();
        AccountFeedback = "此操作需要登录。登录后返回原页面，请再次确认操作。";
        return false;
    }
    private void NotifyAccount()
    {
        foreach (var name in new[] { nameof(IsAccountLoggedIn), nameof(AccountEntryText), nameof(AccountAvatarText), nameof(AccountDisplayNameText), nameof(AccountEmailText), nameof(OnlinePlanText), nameof(OnlineQuotaText), nameof(DeviceCountText), nameof(AccountSessionText), nameof(HasSavedAccountSession), nameof(TranslationServiceText) }) OnPropertyChanged(name);
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
    public string OnlineQuotaText => OnlineUsage == null ? "额度待同步" : $"剩余 {OnlineUsage.Remaining:N0} / {OnlineUsage.MonthlyQuota:N0}";
    public string DeviceCountText => !_devicesSynced ? "设备待同步" : $"已绑定 {OnlineDevices.Count} 台设备";

    [RelayCommand]
    private async Task RefreshAccountAsync()
    {
        if (IsAccountRefreshing || string.IsNullOrWhiteSpace(AppConfig.DecryptApiKey(_config.AuthTokenEncrypted)) || !_apiClient.IsConfigured) return;
        var sessionVersion = _sessionVersion;
        IsAccountRefreshing = true;
        AccountState = AccountSessionState.Validating;
        AccountFeedback = "正在验证会话并同步账户…";
        try
        {
            var profileTask = _apiClient.GetProfileAsync();
            var subscriptionTask = _apiClient.GetSubscriptionAsync();
            var usageTask = _apiClient.GetUsageAsync();
            var devicesTask = _apiClient.GetDevicesAsync();
            await Task.WhenAll(profileTask, subscriptionTask, usageTask, devicesTask).ConfigureAwait(true);
            if (sessionVersion != _sessionVersion) return;
            OnlineProfile = await profileTask;
            _sessionVerified = OnlineProfile != null || _sessionVerified;
            AccountState = OnlineProfile != null ? AccountSessionState.SignedIn : AccountSessionState.Offline;
            AccountFeedback = OnlineProfile == null ? "暂时无法同步账户，请检查网络后重试。" : "账户信息已同步。";
            OnlineSubscription = await subscriptionTask;
            OnlineUsage = await usageTask;
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
        }
        catch (ApiAuthenticationException ex)
        {
            if (sessionVersion != _sessionVersion) return;
            Log.Information("在线账户会话已失效：{ErrorCode}", ex.ErrorCode);
            try { SaveAccountSession(""); } catch (Exception saveEx) { Log.Debug(saveEx, "清除失效会话失败"); }
            _sessionVerified = false;
            _devicesSynced = false;
            AccountState = AccountSessionState.Expired;
            AccountFeedback = "登录已过期，请重新登录。";
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
        catch (Exception ex) { if (sessionVersion != _sessionVersion) return; AccountState = AccountSessionState.Offline; AccountFeedback = "暂时无法连接服务，请检查网络后重试。"; Log.Warning(ex, "刷新在线账户信息失败"); }
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
        if (IsLoggingIn) return;
        if (!_apiClient.IsConfigured) { AccountState = AccountSessionState.ConfigurationError; AccountFeedback = "服务配置异常，请联系管理员修复安装配置后重试。"; return; }
        if (string.IsNullOrWhiteSpace(LoginName) || string.IsNullOrEmpty(password)) { AccountFeedback = "请输入账号和密码。"; return; }
        IsLoggingIn = true;
        AccountState = AccountSessionState.SigningIn;
        AccountFeedback = "正在登录…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _apiClient.LoginAsync(LoginName.Trim(), password, cts.Token);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Token)) { AccountState = AccountSessionState.SignedOut; AccountFeedback = "登录失败，请检查账号、密码后重试。"; return; }
            if (result.ExpiresAt.HasValue && result.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow) { AccountState = AccountSessionState.Expired; AccountFeedback = "登录会话已过期，请重试。"; return; }
            SaveAccountSession(result.Token);
            _sessionVerified = true;
            AccountState = AccountSessionState.SignedIn;
            AccountFeedback = "登录成功。";
            NotifyAccount();
            await RefreshAccountAsync();
            if (IsAccountLoggedIn && _loginReturnPage != null) { var page = _loginReturnPage; _loginReturnPage = null; CurrentPage = page; }
        }
        catch (OperationCanceledException) { AccountState = AccountSessionState.Offline; AccountFeedback = "登录超时，请检查网络后重试。"; }
        catch (IOException) { AccountFeedback = "无法保存登录会话，请检查配置目录权限。"; }
        catch (Exception ex) { AccountState = AccountSessionState.Offline; Log.Warning(ex, "Login failed"); AccountFeedback = "暂时无法登录，请检查网络或稍后重试。"; }
        finally { IsLoggingIn = false; if (AccountState == AccountSessionState.SigningIn) AccountState = AccountSessionState.SignedOut; }
    }

    [RelayCommand]
    private void LogoutAccount()
    {
        if (IsProcessing) { AccountFeedback = "任务正在执行，完成或停止任务后可以退出登录。"; return; }
        try
        {
            SaveAccountSession("");
            _sessionVerified = false;
            _devicesSynced = false;
            AccountState = AccountSessionState.SignedOut;
            AccountFeedback = "已退出登录。";
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
            ToastService.Success("已清除本机登录会话。");
        }
        catch { ToastService.Error("退出未完成：会话清除失败，请检查配置目录。"); }
    }

    private void SaveAccountSession(string token)
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
            throw new InvalidOperationException("Settings path is not initialized.");
        var encrypted = AppConfig.EncryptApiKey(token);
        DwgTranslator.Core.Services.SettingsStore.Update(_settingsPath, c => c.AuthTokenEncrypted = encrypted);
        _config.AuthTokenEncrypted = encrypted;
        _sessionVersion++;
    }
}




