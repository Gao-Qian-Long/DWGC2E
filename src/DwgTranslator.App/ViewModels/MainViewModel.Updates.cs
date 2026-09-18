using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.App.Services;
using DwgTranslator.App.Views.Controls;
using DwgTranslator.Core.Api;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _hasAvailableUpdate;
    [ObservableProperty] private string _updateSummary = "";
    private VersionInfo? _availableUpdate;
    private string? _notifiedVersion;
    private bool _checkingUpdate;
    private readonly CancellationTokenSource _updateLifetime = new();
    private DispatcherTimer? _updateTimer;
    private Window? _updateWindow;

    public void StartUpdateChecks()
    {
        if (_updateTimer != null || _updateLifetime.IsCancellationRequested) return;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(false);
        _updateTimer.Start();
        _ = CheckUpdatesAsync(false);
    }
    private void StopUpdateChecks()
    {
        _updateTimer?.Stop(); _updateLifetime.Cancel(); _updateWindow?.Close();
    }
    public async Task CheckUpdatesAsync(bool manual)
    {
        if (_checkingUpdate || _updateLifetime.IsCancellationRequested || (!manual && !_config.AutoCheckUpdate)) return;
        _checkingUpdate = true;
        if (manual) SettingsFeedback = "正在检查更新…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var current = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "2.1.1";
            var info = await _apiClient.CheckVersionAsync(current, timeout.Token);
            if (_updateLifetime.IsCancellationRequested) return;
            if (info == null || DesktopNotificationPolicy.ParseStableVersion(info.LatestVersion) == null)
            {
                if (manual) SettingsFeedback = "未取得有效的正式版本信息，请稍后重试。";
                return;
            }
            HasAvailableUpdate = DesktopNotificationPolicy.IsNewer(info.LatestVersion, current);
            _availableUpdate = HasAvailableUpdate ? info : null;
            UpdateSummary = HasAvailableUpdate ? $"发现新版本 {info.LatestVersion}，建议更新。" : "当前已是最新版本。";
            if (manual) SettingsFeedback = UpdateSummary;
            if (HasAvailableUpdate && _notifiedVersion != info.LatestVersion)
            {
                _notifiedVersion = info.LatestVersion;
                ToastService.Info(UpdateSummary + " 可点击顶部“建议更新”查看详情。");
            }
            if (manual && HasAvailableUpdate) ShowUpdateDetails();
        }
        catch (Exception) when (!_updateLifetime.IsCancellationRequested)
        { if (manual) SettingsFeedback = "无法检查更新，请检查网络后重试。"; }
        catch (Exception) when (_updateLifetime.IsCancellationRequested) { }
        finally { _checkingUpdate = false; }
    }
    [RelayCommand]
    private void ShowUpdateDetails()
    {
        if (_availableUpdate is not { } info) return;
        if (_updateWindow != null) { _updateWindow.Activate(); return; }
        var address = DesktopNotificationPolicy.DownloadAddress(info.DownloadUrl)
            ?? DesktopNotificationPolicy.DownloadAddress(info.BackupDownloadUrl);
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 25,
            Text = $"发现新版本 {info.LatestVersion}\n当前版本：{AppVersionText}\n\n建议在完成当前翻译或导出任务后更新。不会自动关闭软件或安装更新。\n\n" +
                (string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "本次发布未提供更新说明。" : info.ReleaseNotes) +
                (address == null ? "\n\n下载暂未开放，请稍后重试。" : "") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        var later = new Button { Content = "稍后再说", MinWidth = 88, Margin = new Thickness(0,0,8,0), IsCancel = true };
        later.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        var download = new Button { Content = "前往下载", MinWidth = 88, IsEnabled = address != null };
        download.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        var window = new Window { Title = "软件更新", Owner = Application.Current?.MainWindow, Width = 560, Height = 420, MinWidth = 320, MinHeight = 240,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontSize = 14 };
        window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
        window.SetResourceReference(Window.ForegroundProperty, "Brush.TextPrimary");
        if (window.Owner != null) window.FontFamily = window.Owner.FontFamily;
        later.Click += (_, _) => window.Close();
        download.Click += (_, _) =>
        {
            if (address == null) return;
            try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true }); }
            catch { ToastService.Warning("无法打开浏览器，请稍后重试。"); }
        };
        actions.Children.Add(later); actions.Children.Add(download);
        window.Content = new DialogShell("建议更新软件", body, actions);
        DialogShell.Constrain(window);
        _updateWindow = window; window.Closed += (_, _) => _updateWindow = null;
        window.Show();
    }
}
