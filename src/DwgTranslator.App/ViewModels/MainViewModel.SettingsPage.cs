using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.App.Services;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using Serilog;
namespace DwgTranslator.App.ViewModels;
public partial class MainViewModel
{
    [ObservableProperty] private int _settingsSection;
    [ObservableProperty] private string _aboutFeedback = "可检查软件版本或重新检测云端服务。";
    [ObservableProperty] private bool _isHelpExpanded;
    private Views.HelpWindow? _helpWindow;
    private Views.LogViewerWindow? _logViewerWindow;

    [RelayCommand]
    private void OpenHelp()
    {
        if (_helpWindow is { IsVisible: true })
        {
            if (_helpWindow.WindowState == WindowState.Minimized)
                _helpWindow.WindowState = WindowState.Normal;
            _helpWindow.Activate();
            ToastService.Info("帮助中心已在窗口中打开。");
            return;
        }

        _helpWindow = new Views.HelpWindow { Owner = Application.Current.MainWindow };
        _helpWindow.Closed += (_, _) =>
        {
            _helpWindow = null;
            IsHelpExpanded = false;
        };
        IsHelpExpanded = true;
        _helpWindow.Show();
        ToastService.Success("帮助中心已打开。");
    }

    private void ToggleLogWindow()
    {
        if (_logViewerWindow is { IsVisible: true })
        {
            _logViewerWindow.Close();
            _logViewerWindow = null;
            ToastService.Info("运行日志已关闭。");
            return;
        }

        _logViewerWindow = new Views.LogViewerWindow(LogViewModel) { Owner = Application.Current.MainWindow };
        _logViewerWindow.Closed += (_, _) => _logViewerWindow = null;
        _logViewerWindow.Show();
        ToastService.Success("运行日志已打开。");
    }

    [RelayCommand]
    private void CopyVersionInfo()
    {
        try
        {
            Clipboard.SetText($"QLCAD {AppVersionText}\n{AppBuildText}");
            AboutFeedback = "版本信息已复制。";
            ToastService.Success("版本信息已复制。");
        }
        catch
        {
            AboutFeedback = "复制失败，请稍后重试。";
            ToastService.Warning(AboutFeedback);
        }
    }

    [RelayCommand]
    private void OpenWebsite() => OpenAboutLink("https://cad.pocketter.dpdns.org/", "官网已打开。");

    [RelayCommand]
    private void OpenFeedback() => OpenAboutLink("https://cad.pocketter.dpdns.org/#feedback", "反馈页面已打开。");

    private void OpenAboutLink(string address, string successMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            AboutFeedback = successMessage;
            ToastService.Success(successMessage);
        }
        catch
        {
            AboutFeedback = "无法打开浏览器，请稍后重试。";
            ToastService.Warning(AboutFeedback);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceCheckButtonText))]
    private bool _isCheckingService;
    public string ServiceCheckButtonText => IsCheckingService ? "正在检测…" : "重新检测";
    [ObservableProperty] private string _settingsFeedback = "修改后点击「保存更改」生效。";
    [ObservableProperty] private string _serviceFeedback = "尚未检测服务状态。";
    private AppConfig? _settingsDraft;
    private bool _settingsPendingApply;
    private bool _settingsInputsValid = true;
    private bool _settingsRestartRequired;
    private AppConfig? _settingsBaseline;
    public AppConfig SettingsDraft => _settingsDraft ??= BeginSettingsEdit();
    private static readonly string[] EditableSettings = ["Language", "StartWithWindows", "RestoreLastWorkspace", "AutoCheckUpdate", "OpenOutputFolderAfterExport", "ProtectDimensions", "ProtectTolerances", "ProtectModels", "GlossaryFirst", "MaxTranslationConcurrency", "LocalWorkerCount", "AiConcurrency", "MemoryOptimization", "MaxRetryCount", "ExportDirectory", "OutputNamingPattern", "DuplicatePolicy", "BackupSourceBeforeWrite", "AutoCadInstallPath", "CadPluginPath", "MinimumLogLevel"];

    public bool HasSettingsValidationErrors => !_settingsInputsValid;
    public bool HasPendingSettingsApplication => _settingsPendingApply;
    public bool CanSaveSettings => HasUnsavedSettings && _settingsInputsValid;
    public bool CanDiscardSettings => HasUnsavedSettings || !_settingsInputsValid;
    public string SettingsStateText => HasSettingsValidationErrors
        ? "输入有误"
        : HasUnsavedSettings
            ? "有未保存更改"
            : HasPendingSettingsApplication
                ? "已保存 · 等待当前任务结束后应用"
                : _settingsRestartRequired
                    ? "已保存 · 重启后完成应用"
                    : "已应用";
    private AppConfig BeginSettingsEdit()
    {
        var c = SettingsStore.Read(_settingsPath ?? Path.Combine(App.AppDataDir, "settings.json"));
        // 输出目录是账号维度的：旧默认值（AppData/旧漫游 exports）视为未设置，
        // 未设置时默认 = <安装目录>\exports（见 ResolveExportDirectory / AccountWorkspace）。
        c.ExportDirectory = AccountWorkspace.OutputDirectoryFor(c, App.AppDataDir, SourceDirectoryHint(), DefaultOutputFolderName, App.InstallDir);
        c.StartWithWindows = Services.StartupRegistration.IsEnabled();
        _settingsBaseline = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(c))!;
        return c;
    }
    public Func<bool>? ValidateSettingsInputs { get; set; }
    public bool HasUnsavedSettings => _settingsDraft != null && _settingsBaseline != null && EditableSettings.Any(n => !Equals(typeof(AppConfig).GetProperty(n)!.GetValue(_settingsDraft), typeof(AppConfig).GetProperty(n)!.GetValue(_settingsBaseline)));


    public void NotifySettingsDraftState(bool inputsValid)
    {
        _settingsInputsValid = inputsValid;
        NotifySettingsStateChanged();
    }

    private void NotifySettingsStateChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedSettings));
        OnPropertyChanged(nameof(HasSettingsValidationErrors));
        OnPropertyChanged(nameof(HasPendingSettingsApplication));
        OnPropertyChanged(nameof(CanSaveSettings));
        OnPropertyChanged(nameof(CanDiscardSettings));
        OnPropertyChanged(nameof(SettingsStateText));
        SaveSettingsChangesCommand.NotifyCanExecuteChanged();
        DiscardSettingsChangesCommand.NotifyCanExecuteChanged();
    }

    public bool SaveSettingsPage()
    {
        if (ValidateSettingsInputs?.Invoke() == false || !_settingsInputsValid) { SettingsFeedback = "请修正标红的输入项后保存。"; NotifySettingsStateChanged(); return false; }
        var c = SettingsDraft;
        if (c.LocalWorkerCount is < 1 or > 6 || c.AiConcurrency is < 1 or > 8 || c.MaxRetryCount is < 0 or > 5 || c.MaxTranslationConcurrency is < 1 or > 20) { SettingsFeedback = "并发或重试次数超出允许范围。"; return false; }
        if (!TryValidateOutputNamingPattern(c.OutputNamingPattern, out var namingError)) { SettingsFeedback = namingError; return false; }
        var startupChanged = c.StartWithWindows != _settingsBaseline!.StartWithWindows;
        var languageChanged = !string.Equals(c.Language, _settingsBaseline.Language, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!string.IsNullOrWhiteSpace(c.ExportDirectory))
            {
                if (!Path.IsPathFullyQualified(c.ExportDirectory)) throw new IOException("请选择绝对路径。");
                Directory.CreateDirectory(c.ExportDirectory);
                using var probe = new FileStream(Path.Combine(c.ExportDirectory, ".write-test-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            _ = new OutputPathResolver(c).RenderFileName("总图.dwg", CurrentTargetLang, DateTime.Now);
            if (startupChanged && !Services.StartupRegistration.TrySetEnabled(c.StartWithWindows, out var error)) { SettingsFeedback = "开机启动设置失败：" + error; return false; }
            SettingsStore.Update(_settingsPath ?? Path.Combine(App.AppDataDir, "settings.json"), latest => {
                latest.AccountOutputDirectories ??= new();
                latest.AccountOutputDirectories[_config.ActiveAccountId.Length == 0 ? "guest" : _config.ActiveAccountId] = c.ExportDirectory;
                foreach (var n in EditableSettings)
                {
                    var property = typeof(AppConfig).GetProperty(n)!;
                    if (!Equals(property.GetValue(c), property.GetValue(_settingsBaseline))) property.SetValue(latest, property.GetValue(c));
                }
            });
            _settingsPendingApply = true;
            _settingsRestartRequired |= languageChanged;
            if (!IsProcessing) ApplySavedSettings();
            _settingsDraft = null;
            OnPropertyChanged(nameof(SettingsDraft));
            SettingsFeedback = IsProcessing
                ? (languageChanged ? "已保存；运行参数将在当前任务结束后应用，界面语言将在下次启动后生效。" : "已保存，将在当前任务结束后生效。")
                : (languageChanged ? "已保存；除界面语言外均已应用，界面语言将在下次启动后生效。" : "已保存并应用。 ");
            NotifySettingsStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存设置失败");
            if (startupChanged) Services.StartupRegistration.TrySetEnabled(_settingsBaseline.StartWithWindows, out _);
            SettingsFeedback = "保存失败，请检查路径、命名规则和配置目录权限。修改仍保留。";
            NotifySettingsStateChanged();
            return false;
        }
    }
    private void ApplySavedSettings()
    {
        if (!_settingsPendingApply) return;
        _settingsPendingApply = false;
        LoadConfig();
        if (Enum.TryParse<Serilog.Events.LogEventLevel>(_config.MinimumLogLevel, true, out var level)) App.LogLevelSwitch.MinimumLevel = level;
        (_taskManager as DwgTranslator.Core.Tasks.IRuntimeTaskConfiguration)?.ApplyConfiguration(_config);
        _taskOptions.LocalWorkerCount = _config.LocalWorkerCount;
        _taskOptions.AiConcurrency = _config.AiConcurrency;
        _taskOptions.MaxRetryCount = _config.MaxRetryCount;
        _taskOptions.MemoryOptimization = _config.MemoryOptimization;
        OnPropertyChanged(string.Empty);
        NotifySettingsStateChanged();
    }
    public bool ConfirmLeavePage()
    {
        if (!ConfirmLeaveProofreading()) return false;
        if (HasUnsavedSettings || ValidateSettingsInputs?.Invoke() == false)
        {
            var result = Views.PromptDialog.Show("设置尚未保存。是否保存后继续？", "未保存的设置", System.Windows.MessageBoxButton.YesNoCancel);
            if (result == System.Windows.MessageBoxResult.Cancel) return false;
            if (result == System.Windows.MessageBoxResult.Yes && !SaveSettingsPage()) return false;
            if (result == System.Windows.MessageBoxResult.No) DiscardSettingsChangesCore();
        }
        return ConfirmLeaveGlossary();
    }
    [RelayCommand]
    private void SaveSettingsChanges() => SaveSettingsPage();

    [RelayCommand]
    private void DiscardSettingsChanges()
    {
        // §L4 放弃未保存的设置没有撤销入口，需二次确认；无未保存内容时静默返回，不弹窗。
        if (!HasUnsavedSettings && _settingsInputsValid) return;
        if (!Views.ConfirmDialog.Ask(
                Application.Current?.MainWindow,
                "放弃更改",
                "将丢弃尚未保存的设置更改，已应用的配置不受影响。确定放弃吗？",
                confirmText: "放弃更改",
                danger: true))
            return;

        DiscardSettingsChangesCore();
    }

    private void DiscardSettingsChangesCore()
    {
        _settingsDraft = null;
        _settingsInputsValid = true;
        OnPropertyChanged(nameof(SettingsDraft));
        SettingsFeedback = "已放弃未保存更改，当前仍使用已应用的配置。";
        NotifySettingsStateChanged();
    }

    [RelayCommand]
    private void RestoreSettingsSectionDefaults()
    {
        if (SettingsSection is < 0 or > 4) return;
        var result = Views.PromptDialog.Show("将本节恢复为推荐值？修改仍需点击“保存更改”后才会应用。", "恢复推荐值", MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes) return;

        var draft = SettingsDraft;
        var recommended = new AppConfig();
        switch (SettingsSection)
        {
            case 0:
                draft.Language = recommended.Language;
                draft.StartWithWindows = recommended.StartWithWindows;
                draft.AutoCheckUpdate = recommended.AutoCheckUpdate;
                draft.OpenOutputFolderAfterExport = recommended.OpenOutputFolderAfterExport;
                break;
            case 1:
                draft.ProtectDimensions = recommended.ProtectDimensions;
                draft.ProtectTolerances = recommended.ProtectTolerances;
                draft.ProtectModels = recommended.ProtectModels;
                draft.GlossaryFirst = recommended.GlossaryFirst;
                draft.LocalWorkerCount = recommended.LocalWorkerCount;
                draft.AiConcurrency = recommended.AiConcurrency;
                draft.MaxTranslationConcurrency = recommended.MaxTranslationConcurrency;
                draft.MaxRetryCount = recommended.MaxRetryCount;
                draft.MemoryOptimization = recommended.MemoryOptimization;
                break;
            case 2:
                // 输出目录属于用户数据位置，恢复推荐值时不清空。
                draft.OutputNamingPattern = recommended.OutputNamingPattern;
                draft.DuplicatePolicy = recommended.DuplicatePolicy;
                draft.BackupSourceBeforeWrite = recommended.BackupSourceBeforeWrite;
                break;
            case 3:
                var detection = AutoCadDetector.DetectInstallation();
                draft.AutoCadInstallPath = detection.Found ? detection.InstallPath : string.Empty;
                draft.CadPluginPath = AutoCadDetector.FindCadPlugin() ?? string.Empty;
                break;
            case 4:
                draft.MinimumLogLevel = "Information";
                break;
        }

        OnPropertyChanged(nameof(SettingsDraft));
        SettingsFeedback = SettingsSection == 2
            ? "已恢复本节推荐值；为避免丢失用户选择，输出目录保持不变。尚未保存。"
            : "已恢复本节推荐值，尚未保存。";
        NotifySettingsStateChanged();
    }

    private static bool TryValidateOutputNamingPattern(string? pattern, out string error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "命名规则不能为空。";
            return false;
        }
        if (pattern.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0)
        {
            error = "命名规则包含路径分隔符或文件名非法字符。";
            return false;
        }
        var reduced = pattern
            .Replace("{name}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{lang}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (reduced.Contains('{') || reduced.Contains('}'))
        {
            error = "命名规则仅支持 {name}、{lang}、{date} 三个变量。";
            return false;
        }
        error = string.Empty;
        return true;
    }
    [RelayCommand] private Task CheckForUpdateAsync() => CheckUpdatesAsync(true);
    [RelayCommand] private async Task CheckServiceAsync()
    {
        if (IsCheckingService) { ServiceFeedback = "正在检测服务，请稍候…"; ToastService.Info("正在检测服务，请稍候。"); return; }
        IsCheckingService = true; ServiceFeedback = "正在检测服务…";
        try
        {
            if (!_apiClient.IsConfigured) { ServiceFeedback = "服务配置异常，请联系管理员修复安装配置。"; ToastService.Warning(ServiceFeedback); return; }
            var version = await _apiClient.CheckVersionAsync("0.0.0");
            ServiceFeedback = version == null ? "服务未返回有效响应，请稍后重试。" : "服务连接正常。";
            if (version == null) ToastService.Warning(ServiceFeedback); else ToastService.Success(ServiceFeedback);
        }
        catch (Exception ex) { Log.Warning(ex, "服务连接检查失败"); ServiceFeedback = "服务连接失败，请检查网络后重试。"; ToastService.Warning(ServiceFeedback); }
        finally { IsCheckingService = false; }
    }
    [RelayCommand] private void DetectSettingsCad()
    {
        var detection = AutoCadDetector.DetectInstallation();
        if (detection.Found) SettingsDraft.AutoCadInstallPath = detection.InstallPath;
        SettingsDraft.CadPluginPath = AutoCadDetector.FindCadPlugin() ?? string.Empty;
        OnPropertyChanged(nameof(SettingsDraft));
        SettingsFeedback = detection.Found ? "检测完成，请保存路径后运行环境检查。" : "未检测到 CAD，请手动选择安装目录。";
    }
}
