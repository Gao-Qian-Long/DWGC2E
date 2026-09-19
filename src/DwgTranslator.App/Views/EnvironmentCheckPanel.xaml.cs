using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Services;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DwgTranslator.App.Views;

/// <summary>
/// Environment self-check and CAD plugin installation.
///
/// This is the screen a user needs when the program is handed to somebody else: it reports the
/// facts that decide whether the program can run (runtime, CAD product, plugin, API access) and
/// performs the one setup step that cannot be automated at publish time, namely copying the plugin
/// into the CAD installation.
/// </summary>
public partial class EnvironmentCheckPanel : System.Windows.Controls.UserControl
{
    private readonly MainViewModel _viewModel;
    private readonly Brush _ok;
    private readonly Brush _warn;
    private readonly Brush _unknown;
    private bool _firstCheckPrompted;

    public EnvironmentCheckPanel(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;

        _ok = (Brush)FindResource("Brush.Success");
        _warn = (Brush)FindResource("Brush.Warning");
        _unknown = (Brush)FindResource("Brush.TextDisabled");

        Loaded += (_, _) => { RefreshStatus(); Dispatcher.BeginInvoke(new Action(PromptInstallIfNeeded), System.Windows.Threading.DispatcherPriority.ApplicationIdle); };
    }

    private void RefreshStatus()
    {
        // App
        AppVersionText.Text = $"{_viewModel.AppVersionText}（DWG Translator）";
        AppLamp.Fill = _ok;

        bool selfContained = _viewModel.IsSelfContained;
        RuntimeText.Text = selfContained
            ? "自包含发布，目标电脑无需安装 .NET 运行时"
            : "依赖框架（本机需已安装 .NET 8 桌面运行时）";
        RuntimeLamp.Fill = selfContained ? _ok : _warn;

        DataPathText.Text = _viewModel.AppDataDirectory;
        DataPathText.ToolTip = _viewModel.AppDataDirectory;
        PathsLamp.Fill = Directory.Exists(_viewModel.AppDataDirectory) ? _ok : _unknown;

        // CAD
        var cadPath = _viewModel.ResolveCadInstallPath();
        var pluginSource = _viewModel.CadPluginDirectory;
        if (string.IsNullOrWhiteSpace(cadPath) || !AutoCadDetector.IsValidAutoCadPath(cadPath))
        {
            CadProductText.Text = "未检测到 CAD（可在设置中手动指定安装目录）";
            CadLamp.Fill = _warn;
            PluginStatusText.Text = "无法安装（未找到 CAD）";
            PluginLamp.Fill = _warn;
            AutoLoadText.Text = "-";
            AutoLoadLamp.Fill = _unknown;
            InstallButton.IsEnabled = false;
            UninstallButton.IsEnabled = false;
        }
        else
        {
            var status = CadPluginInstaller.Inspect(cadPath, pluginSource);
            CadProductText.Text = $"{status.CadProductName}  ·  {status.CadInstallPath}";
            CadProductText.ToolTip = status.CadInstallPath;
            CadLamp.Fill = _ok;

            PluginStatusText.Text = status.PluginFilesPresent
                ? (status.PluginUpToDate ? $"已安装且与当前程序一致（{status.InstalledPluginVersion}）" : "已安装，但版本与当前程序不一致，建议修复")
                : "尚未安装到 CAD";
            PluginLamp.Fill = status.PluginFilesPresent && status.PluginUpToDate ? _ok : _warn;

            AutoLoadText.Text = status.AutoLoadConfigured
                ? "已配置（打开 CAD 即自动加载插件）"
                : "未配置";
            AutoLoadLamp.Fill = status.AutoLoadConfigured ? _ok : _warn;

            InstallButton.Content = status.PluginFilesPresent ? "修复 / 更新插件" : "安装插件";
            InstallButton.IsEnabled = true;
            InstallButton.Visibility = status.PluginFilesPresent && status.PluginUpToDate && status.AutoLoadConfigured ? Visibility.Collapsed : Visibility.Visible;
            UninstallButton.IsEnabled = status.PluginFilesPresent || status.AutoLoadConfigured;
        }

        // Translation service
        ApiEndpointText.Text = _viewModel.TranslationServiceText;
        ApiLamp.Fill = _viewModel.TranslationServiceText.Contains("异常", StringComparison.Ordinal) ? _warn : _ok;
    }


    private void PromptInstallIfNeeded()
    {
        if (_firstCheckPrompted || !IsVisible) return;
        _firstCheckPrompted = true;
        var cadPath = _viewModel.ResolveCadInstallPath();
        if (string.IsNullOrWhiteSpace(cadPath) || !AutoCadDetector.IsValidAutoCadPath(cadPath)) return;
        var status = CadPluginInstaller.Inspect(cadPath, _viewModel.CadPluginDirectory);
        if (status.PluginFilesPresent && status.AutoLoadConfigured) return;
        var answer = PromptDialog.Show(
            "已检测到 CAD，但当前电脑尚未安装 DWGC2E CAD 插件。\n\n安装插件后，翻译结果才能直接写回 CAD 图纸；插件文件已随本 APP 内置，不需要另行下载。\n\n现在安装吗？",
            "安装 CAD 插件", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Install_Click(this, new RoutedEventArgs());
        else AppendLog("用户暂不安装 CAD 插件；翻译和导出功能仍可继续使用。");
    }

    private void AppendLog(string line) =>
        LogText.AppendText((LogText.Text.Length > 0 ? Environment.NewLine : string.Empty) + line);

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        LogText.Clear();
        AppendLog("重新检测 CAD 安装…");
        RefreshStatus();
        AppendLog("检测完成。");
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;
        try
        {
            if (new[] { "acad", "acadlt", "gcad" }.Any(name => System.Diagnostics.Process.GetProcessesByName(name).Length > 0))
            {
                AppendLog("检测到 CAD 正在运行：为避免插件文件被占用，未执行修复。请保存图纸并关闭 CAD 后重试。");
                PromptDialog.Show("修复插件前请保存图纸并关闭 CAD。程序不会强制关闭 CAD。", "请先关闭 CAD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var cadPath = _viewModel.ResolveCadInstallPath();
            var pluginSource = _viewModel.CadPluginDirectory;
            AppendLog($"插件来源：{pluginSource}");
            AppendLog($"目标 CAD：{cadPath}");

            var result = await System.Threading.Tasks.Task.Run(
                () => CadPluginInstaller.Install(cadPath, pluginSource));

            foreach (var step in result.Steps) AppendLog("· " + step);
            AppendLog(result.Success ? "完成：" + result.Summary : "失败：" + result.Summary);
            if (!result.Success && result.Error != null) AppendLog(result.Error);
        }
        catch (Exception ex)
        {
            AppendLog("异常：" + ex.Message);
        }
        finally
        {
            RefreshStatus();
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var cadPath = _viewModel.ResolveCadInstallPath();
        if (DwgTranslator.App.Views.PromptDialog.Show($"确定要从 {cadPath} 中卸载 CAD 插件吗？\n桌面程序不受影响。", "卸载插件",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        InstallButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => CadPluginInstaller.Uninstall(cadPath));
            foreach (var step in result.Steps) AppendLog("· " + step);
            AppendLog(result.Success ? "完成：" + result.Summary : "失败：" + result.Summary);
        }
        catch (Exception ex)
        {
            AppendLog("异常：" + ex.Message);
        }
        finally
        {
            RefreshStatus();
        }
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e) => _viewModel.OpenFolder(_viewModel.AppDataDirectory);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => _viewModel.OpenFolder(_viewModel.LogDirectory);


}
