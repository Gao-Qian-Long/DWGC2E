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

public partial class MainViewModel
{
    [ObservableProperty] private ProfileInfo? _onlineProfile;
    [ObservableProperty] private SubscriptionInfo? _onlineSubscription;
    [ObservableProperty] private UsageInfo? _onlineUsage;
    [ObservableProperty] private bool _isAccountRefreshing;
    public ObservableCollection<DeviceInfo> OnlineDevices { get; } = new();
    public bool IsAccountLoggedIn => !string.IsNullOrWhiteSpace(AppConfig.DecryptApiKey(_config.AuthTokenEncrypted));
    public string AccountAvatarText
    {
        get
        {
            var name = (OnlineProfile?.DisplayName ?? "U").Trim();
            return name.Length == 0 ? "U" : name[..Math.Min(2, name.Length)].ToUpperInvariant();
        }
    }
    public string AccountDisplayNameText => string.IsNullOrWhiteSpace(OnlineProfile?.DisplayName) ? "未登录" : OnlineProfile.DisplayName;
    public string AccountEmailText => OnlineProfile?.Email ?? string.Empty;
    public string OnlinePlanText => string.IsNullOrWhiteSpace(OnlineSubscription?.PlanName) ? "未同步套餐" : OnlineSubscription.PlanName;
    public string OnlineQuotaText => OnlineUsage == null ? "额度待同步" : $"剩余 {OnlineUsage.Remaining:N0} / {OnlineUsage.MonthlyQuota:N0}";
    public string DeviceCountText => $"已绑定 {OnlineDevices.Count} 台设备";

    [RelayCommand]
    private async Task RefreshAccountAsync()
    {
        if (IsAccountRefreshing || string.IsNullOrWhiteSpace(AppConfig.DecryptApiKey(_config.AuthTokenEncrypted)) || !_apiClient.IsConfigured) return;
        IsAccountRefreshing = true;
        try
        {
            var profileTask = _apiClient.GetProfileAsync();
            var subscriptionTask = _apiClient.GetSubscriptionAsync();
            var usageTask = _apiClient.GetUsageAsync();
            var devicesTask = _apiClient.GetDevicesAsync();
            await Task.WhenAll(profileTask, subscriptionTask, usageTask, devicesTask).ConfigureAwait(true);
            OnlineProfile = await profileTask;
            OnlineSubscription = await subscriptionTask;
            OnlineUsage = await usageTask;
            var devices = await devicesTask;
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
            Log.Information("在线账户会话已失效：{ErrorCode}", ex.ErrorCode);
            try { SaveAccountSession(""); } catch (Exception saveEx) { Log.Debug(saveEx, "清除失效会话失败"); }
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
            ToastService.Warning("登录已过期，请重新登录。");
        }
        catch (Exception ex) { Log.Warning(ex, "刷新在线账户信息失败"); }
        finally { IsAccountRefreshing = false; }
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
        var answer = MessageBox.Show($"确定要移除设备“{device.DeviceName}”吗？", "移除设备", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            if (!await _apiClient.RevokeDeviceAsync(device.DeviceId))
            {
                ToastService.Error("设备移除失败，请稍后重试。");
                return;
            }
            OnlineDevices.Remove(device);
            OnPropertyChanged(nameof(DeviceCountText));
            ToastService.Success("设备已移除。");
        }
        catch (Exception ex) { Log.Warning(ex, "移除设备失败"); ToastService.Error("设备移除失败，请稍后重试。"); }
    }

    public string AccountSessionText => string.IsNullOrEmpty(AppConfig.DecryptApiKey(_config.AuthTokenEncrypted))
        ? "尚未登录" : "已保存登录会话";

    [RelayCommand]
    private void LoginAccount()
    {
        if (IsProcessing) return;
        if (_apiClient.ModeName != "worker" || !_apiClient.IsConfigured)
        {
            ToastService.Warning("请配置 Worker 服务地址，并重启应用以切换服务模式。");
            return;
        }
        var dialog = new Views.LoginDialog(async (account, password, ct) =>
        {
            var result = await _apiClient.LoginAsync(account, password, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.Success || string.IsNullOrWhiteSpace(result.Token))
                return ApiErrorMessages.Describe(result.ErrorCode, result.Message ?? "登录未返回有效会话。");
            if (result.ExpiresAt.HasValue && result.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow)
                return "服务返回的会话已过期，请重新登录。";
            try { SaveAccountSession(result.Token); }
            catch { return "登录成功，但会话保存失败。请检查配置目录后重试。"; }
            return null;
        }) { Owner = Application.Current?.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            OnPropertyChanged(nameof(IsAccountLoggedIn));
            OnPropertyChanged(nameof(AccountAvatarText));
            OnPropertyChanged(nameof(AccountSessionText));
            _ = RefreshAccountAsync();
            ToastService.Success("登录成功。");
        }
    }

    [RelayCommand]
    private void LogoutAccount()
    {
        if (IsProcessing) return;
        try
        {
            SaveAccountSession("");
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
            ToastService.Success("已清除本机登录会话。");
        }
        catch { ToastService.Error("退出未完成：会话清除失败，请检查配置目录。"); }
    }

    private void SaveAccountSession(string token)
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
            throw new InvalidOperationException("Settings path is not initialized.");
        var encrypted = AppConfig.EncryptApiKey(token);
        var snapshot = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(_config, ConfigWriteOptions), AppConfigJson.ReadOptions)!;
        snapshot.DeepSeekApiKey = AppConfig.EncryptApiKey(_config.DeepSeekApiKey);
        snapshot.AuthTokenEncrypted = encrypted;
        var temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, ConfigWriteOptions));
            File.Move(temporary, _settingsPath, overwrite: true);
            _config.AuthTokenEncrypted = encrypted; // publish in memory only after persistence succeeds
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}




