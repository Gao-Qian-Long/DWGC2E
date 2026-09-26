using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.App.Services;
using DwgTranslator.App.Views.Controls;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public enum UpdateCheckState
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Failed
}

public partial class MainViewModel
{
    [ObservableProperty] private UpdateCheckState _updateState = UpdateCheckState.Idle;
    [ObservableProperty] private string _updateSummary = "";
    public bool IsCheckingUpdate => UpdateState == UpdateCheckState.Checking;
    public bool UpdateCheckFailed => UpdateState == UpdateCheckState.Failed;
    public bool UpdateCheckSucceeded => UpdateState is UpdateCheckState.UpToDate or UpdateCheckState.UpdateAvailable;
    public bool HasAvailableUpdate => _availableUpdate != null;
    public string UpdateStatusText => UpdateState switch
    {
        UpdateCheckState.Checking => "正在检查更新…",
        UpdateCheckState.Failed => "暂时无法连接更新服务，请稍后重试",
        UpdateCheckState.UpdateAvailable => UpdateSummary,
        UpdateCheckState.UpToDate => "当前已是最新版本",
        _ => "尚未检查更新"
    };
    public string UpdateCheckButtonText => IsCheckingUpdate ? "正在检查…" : "检查更新";
    partial void OnUpdateStateChanged(UpdateCheckState value)
    {
        OnPropertyChanged(nameof(IsCheckingUpdate));
        OnPropertyChanged(nameof(UpdateCheckFailed));
        OnPropertyChanged(nameof(UpdateCheckSucceeded));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(UpdateCheckButtonText));
    }
    partial void OnUpdateSummaryChanged(string value) => OnPropertyChanged(nameof(UpdateStatusText));
    private VersionInfo? _availableUpdate;
    private string? _notifiedVersion;
    private void SetAvailableUpdate(VersionInfo? value)
    {
        if (ReferenceEquals(_availableUpdate, value)) return;
        _availableUpdate = value;
        OnPropertyChanged(nameof(HasAvailableUpdate));
    }
    private readonly CancellationTokenSource _updateLifetime = new();
    private DispatcherTimer? _updateTimer;
    private Window? _updateWindow;
    private bool _updateChecksStopped;

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
        // Cancel before Dispose: the in-flight check's catch filter reads IsCancellationRequested, so a
        // token disposed mid-check still lands in the cancellation branch instead of going unobserved.
        if (_updateChecksStopped) return;
        _updateChecksStopped = true;
        if (_updateTimer != null) { _updateTimer.Stop(); _updateTimer = null; }
        _updateWindow?.Close(); _updateWindow = null;
        _updateLifetime.Cancel();
        _updateLifetime.Dispose();
    }
    public async Task CheckUpdatesAsync(bool manual)
    {
        if (IsCheckingUpdate)
        {
            if (manual) { AboutFeedback = "正在检查更新，请稍候…"; ToastService.Info("正在检查更新，请稍候。"); }
            return;
        }
        if (_updateLifetime.IsCancellationRequested || (!manual && !_config.AutoCheckUpdate)) return;
        UpdateState = UpdateCheckState.Checking;
        if (manual) AboutFeedback = "正在检查更新…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var current = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "2.1.1";
            var info = await _apiClient.CheckVersionAsync(current, timeout.Token);
            if (_updateLifetime.IsCancellationRequested) return;
            if (info == null || DesktopNotificationPolicy.ParseStableVersion(info.LatestVersion) == null)
            {
                UpdateState = UpdateCheckState.Failed;
                if (manual) { AboutFeedback = "未取得有效的正式版本信息，请稍后重试。"; ToastService.Warning(AboutFeedback); }
                return;
            }
            var hasAvailableUpdate = DesktopNotificationPolicy.IsNewer(info.LatestVersion, current);
            SetAvailableUpdate(hasAvailableUpdate ? info : null);
            UpdateSummary = hasAvailableUpdate ? $"发现新版本 {info.LatestVersion}，建议更新。" : "当前已是最新版本。";
            UpdateState = hasAvailableUpdate ? UpdateCheckState.UpdateAvailable : UpdateCheckState.UpToDate;
            if (manual) { AboutFeedback = UpdateSummary; if (!HasAvailableUpdate) ToastService.Success("检查完成，当前已是最新版本。"); }
            if (HasAvailableUpdate && _notifiedVersion != info.LatestVersion)
            {
                _notifiedVersion = info.LatestVersion;
                ToastService.Info(UpdateSummary + " 可点击顶部“建议更新”查看详情。");
            }
            if (manual && HasAvailableUpdate) ShowUpdateDetails();
        }
        catch (Exception ex) when (!_updateLifetime.IsCancellationRequested)
        { Log.Warning(ex, "检查更新失败"); UpdateState = UpdateCheckState.Failed; if (manual) { AboutFeedback = "无法检查更新，请检查网络后重试。"; ToastService.Warning(AboutFeedback); } }
        catch (Exception ex) when (_updateLifetime.IsCancellationRequested)
        {
            // Shutdown/disposal cancels the check; keep it recorded but out of the warning stream.
            Log.Debug(ex, "检查更新已取消");
            if (UpdateState == UpdateCheckState.Checking) UpdateState = UpdateCheckState.Idle;
        }
    }
    [RelayCommand]
    private void ShowUpdateDetails()
    {
        if (_availableUpdate is not { } info) return;
        if (_updateWindow != null) { _updateWindow.Activate(); return; }
        var canInstall = SecureUpdatePackageService.HasInstallMetadata(info)
                            && UpdatePackageVerifier.IsTrustedKeyId(info.SigningKeyId);
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 25,
            Text = $"发现新版本 {info.LatestVersion}\n当前版本：{AppVersionText}\n\n" +
                (string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "本次发布未提供更新说明。" : info.ReleaseNotes) +
                (canInstall ? "\n\n安装包具备大小、SHA-256 和签名元数据，可在 APP 内安全下载、校验并升级。" : "\n\n该版本缺少完整签名元数据或签名密钥不匹配，目前只显示更新信息，不执行自动安装。") };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0,8,0,0), Visibility = Visibility.Collapsed };
        var content = new StackPanel(); content.Children.Add(body); content.Children.Add(status); content.Children.Add(progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        var later = new Button { Content = "稍后再说", MinWidth = 88, Margin = new Thickness(0,0,8,0), IsCancel = true };
        later.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        var download = new Button { Content = "下载并准备更新", MinWidth = 128, IsEnabled = canInstall };
        download.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        var window = new Window { Title = "软件更新", Owner = Application.Current?.MainWindow, Width = 580, Height = 460, MinWidth = 360, MinHeight = 280,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontSize = 14 };
        window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
        window.SetResourceReference(Window.ForegroundProperty, "Brush.TextPrimary");
        if (window.Owner != null) window.FontFamily = window.Owner.FontFamily;
        CancellationTokenSource? downloadCts = null;
        later.Click += (_, _) => { if (downloadCts != null) { downloadCts.Cancel(); status.Text = "正在取消下载…"; } else window.Close(); };
        download.Click += async (_, _) =>
        {
            if (!canInstall || downloadCts != null) return;
            downloadCts = new CancellationTokenSource(); download.IsEnabled = false; later.Content = "取消"; progress.Visibility = Visibility.Visible;
            try
            {
                status.Text = "正在下载并验证更新包…";
                var reporter = new Progress<double>(value => progress.Value = value);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                var service = new SecureUpdatePackageService(http, UpdateTrust.PublicKeyPem);
                var updateRoot = Path.Combine(App.AppDataDir, "updates");
                var staged = await service.DownloadAndStageAsync(info, updateRoot, reporter, downloadCts.Token);
                progress.Value = 100; status.Text = "安装包验证完成。升级前请保存图纸并关闭 CAD。";
                if (new[] { "acad", "acadlt", "gcad" }.Any(name => Process.GetProcessesByName(name).Length > 0))
                {
                    status.Text = "检测到 CAD 仍在运行。请保存图纸并关闭 CAD，然后重新点击“下载并准备更新”；已验证包不会写入程序目录。";
                    ToastService.Warning("修复或更新前必须先关闭 CAD，APP 不会强制结束 CAD。 ");
                    return;
                }
                if (ActiveTranslationProject != null && !SaveActiveProject())
                { status.Text = "当前项目未能安全保存，更新已取消。请处理项目冲突后重试。"; return; }
                var verificationText = staged.PackageType == "setup-exe"
                    ? "安装包已通过签名与 SHA-256 校验；APP 退出后更新器会再次校验安装包，再由正式 Setup 完成升级。"
                    : "更新包已通过签名、完整受管文件清单和逐文件哈希校验；APP 退出后更新器还会对原始 ZIP 再次校验。";
                if (Views.PromptDialog.Show(verificationText + "\n\n现在将关闭 APP 并安装更新，完成后自动重启。是否继续？",
                    "准备安装更新", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                { status.Text = "更新包已验证并暂存，尚未修改当前程序。"; return; }
                // 脚本必须落在 staging 之外：staging 是更新包里的暂存内容，不能当作可执行代码来源。
var scriptDirectory = Path.Combine(updateRoot, "apply-" + Guid.NewGuid().ToString("N"));
var script = UpdateApplyScript.Write(scriptDirectory);
                var log = Path.Combine(App.AppDataDir, "logs", $"update-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass"); psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(script);
                psi.ArgumentList.Add("-AppPid"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
                psi.ArgumentList.Add("-Package"); psi.ArgumentList.Add(staged.PackagePath);
                psi.ArgumentList.Add("-PackageSha256"); psi.ArgumentList.Add(staged.PackageSha256);
                psi.ArgumentList.Add("-PackageType"); psi.ArgumentList.Add(staged.PackageType);
                psi.ArgumentList.Add("-Install"); psi.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)); psi.ArgumentList.Add("-Log"); psi.ArgumentList.Add(log);
                var updater = Process.Start(psi) ?? throw new InvalidOperationException("无法启动独立更新器。");
                window.Close(); Application.Current?.Shutdown();
            }
            catch (OperationCanceledException) { status.Text = "已取消更新下载，当前版本未被修改。"; }
            catch (Exception ex) { status.Text = $"更新准备失败：{ex.Message}\n日志目录：{Path.Combine(App.AppDataDir, "logs")}"; ToastService.Warning("更新准备失败，当前版本保持不变。 "); }
            finally { downloadCts?.Dispose(); downloadCts = null; later.Content = "稍后再说"; download.IsEnabled = canInstall; }
        };
        actions.Children.Add(later); actions.Children.Add(download);
        window.Content = new DialogShell("软件更新", content, actions);
        DialogShell.Constrain(window);
        _updateWindow = window; window.Closed += (_, _) => { downloadCts?.Cancel(); _updateWindow = null; };
        window.Show();
    }
}
