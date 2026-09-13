using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.IO;
using System.Text.Json;
namespace DwgTranslator.App.ViewModels;
public partial class MainViewModel
{
    [ObservableProperty] private int _settingsSection;
    [ObservableProperty] private string _settingsFeedback = "更改后点击保存。正在执行的任务不受影响。";
    private AppConfig? _settingsDraft;
    private bool _settingsPendingApply;
    private AppConfig? _settingsBaseline;
    public AppConfig SettingsDraft => _settingsDraft ??= BeginSettingsEdit();
    private static readonly string[] EditableSettings = ["Language", "StartWithWindows", "AutoCheckUpdate", "OpenOutputFolderAfterExport", "ProtectDimensions", "ProtectTolerances", "ProtectModels", "GlossaryFirst", "MaxTranslationConcurrency", "LocalWorkerCount", "AiConcurrency", "MemoryOptimization", "MaxRetryCount", "ExportDirectory", "OutputNamingPattern", "DuplicatePolicy", "BackupSourceBeforeWrite", "AllowOverwriteSource", "AutoCadInstallPath", "CadPluginPath", "MinimumLogLevel"];
    private AppConfig BeginSettingsEdit()
    {
        var c = SettingsStore.Read(_settingsPath ?? Path.Combine(App.AppDataDir, "settings.json"));
        c.StartWithWindows = Services.StartupRegistration.IsEnabled();
        _settingsBaseline = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(c))!;
        return c;
    }
    public Func<bool>? ValidateSettingsInputs { get; set; }
    public bool HasUnsavedSettings => _settingsDraft != null && EditableSettings.Any(n => !Equals(typeof(AppConfig).GetProperty(n)!.GetValue(_settingsDraft), typeof(AppConfig).GetProperty(n)!.GetValue(_settingsBaseline)));
    public bool SaveSettingsPage()
    {
        if (ValidateSettingsInputs?.Invoke() == false) { SettingsFeedback = "请修正标红的输入项后保存。"; return false; }
        var c = SettingsDraft;
        if (c.LocalWorkerCount is < 1 or > 6 || c.AiConcurrency is < 1 or > 8 || c.MaxRetryCount is < 0 or > 5 || c.MaxTranslationConcurrency is < 1 or > 20) { SettingsFeedback = "并发或重试次数超出允许范围。"; return false; }
        if (string.IsNullOrWhiteSpace(c.ExportDirectory) || string.IsNullOrWhiteSpace(c.OutputNamingPattern)) { SettingsFeedback = "输出目录和命名规则不能为空。"; return false; }
        var startupChanged = c.StartWithWindows != _settingsBaseline!.StartWithWindows;
        try
        {
            Directory.CreateDirectory(c.ExportDirectory);
            _ = new OutputPathResolver(c).RenderFileName("总图.dwg", CurrentTargetLang, DateTime.Now);
            if (startupChanged && !Services.StartupRegistration.TrySetEnabled(c.StartWithWindows, out var error)) { SettingsFeedback = "开机启动设置失败：" + error; return false; }
            SettingsStore.Update(_settingsPath ?? Path.Combine(App.AppDataDir, "settings.json"), latest => {
                foreach (var n in EditableSettings)
                {
                    var property = typeof(AppConfig).GetProperty(n)!;
                    if (!Equals(property.GetValue(c), property.GetValue(_settingsBaseline))) property.SetValue(latest, property.GetValue(c));
                }
            });
            _settingsPendingApply = true;
            if (!IsProcessing) ApplySavedSettings();
            _settingsDraft = null;
            OnPropertyChanged(nameof(SettingsDraft));
            SettingsFeedback = IsProcessing ? "已保存，将在当前任务结束后生效。" : "已保存。界面语言更改将在下次启动后生效。";
            return true;
        }
        catch (Exception)
        {
            if (startupChanged) Services.StartupRegistration.TrySetEnabled(_settingsBaseline.StartWithWindows, out _);
            SettingsFeedback = "保存失败，请检查路径、命名规则和配置目录权限。修改仍保留。";
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
    }
    public bool ConfirmLeavePage()
    {
        if (HasUnsavedSettings || ValidateSettingsInputs?.Invoke() == false)
        {
            var result = Views.PromptDialog.Show("设置尚未保存。是否保存后继续？", "未保存的设置", System.Windows.MessageBoxButton.YesNoCancel);
            if (result == System.Windows.MessageBoxResult.Cancel) return false;
            if (result == System.Windows.MessageBoxResult.Yes && !SaveSettingsPage()) return false;
            if (result == System.Windows.MessageBoxResult.No) DiscardSettingsChanges();
        }
        return ConfirmLeaveGlossary();
    }
    [RelayCommand] private void SaveSettingsChanges() => SaveSettingsPage();
    [RelayCommand] private void DiscardSettingsChanges() { _settingsDraft = null; OnPropertyChanged(nameof(SettingsDraft)); SettingsFeedback = "已放弃未保存更改。"; }
    [RelayCommand] private async Task CheckForUpdateAsync()
    {
        SettingsFeedback = "正在检查更新…";
        try { var version = await _apiClient.CheckVersionAsync(typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "2.1.0"); SettingsFeedback = version == null ? "未取得更新信息，请稍后重试。" : "服务发布版本：" + version.LatestVersion + "；当前版本：" + AppVersionText; }
        catch { SettingsFeedback = "无法检查更新，请检查网络后重试。"; }
    }
    [RelayCommand] private async Task CheckServiceAsync()
    {
        SettingsFeedback = "正在检测服务…";
        if (!_apiClient.IsConfigured) { SettingsFeedback = "服务配置异常，请联系管理员修复安装配置。"; return; }
        try { var version = await _apiClient.CheckVersionAsync("0.0.0"); SettingsFeedback = version == null ? "服务未返回有效响应，请稍后重试。" : "服务连接正常。"; }
        catch { SettingsFeedback = "服务连接失败，请检查网络后重试。"; }
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
