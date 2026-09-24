using System.IO;
using System.Windows;
using System.Windows.Input;
namespace DwgTranslator.App.Views;
public partial class ExportModeDialog : Window
{
    public enum ExportMode { AutoCAD, Offline, Cancel }
    public ExportMode SelectedMode { get; private set; } = ExportMode.Cancel;
    public string? TemporaryDirectory { get; private set; }
    public ExportModeDialog(bool autoCadAvailable, bool isTranslation = false)
    {
        InitializeComponent();
        CadOption.IsEnabled = autoCadAvailable;
        CadOption.IsChecked = autoCadAvailable;
        OfflineOption.IsChecked = !autoCadAvailable;
        SubtitleText.Text = isTranslation
            ? "这里选择已有译文如何写入 DWG/DXF；需要 AI 翻译时，仍须登录并联网。"
            : "这里选择如何将文字写入 DWG/DXF。AI 翻译由云端服务完成，需要登录并联网。";
        CadStatus.Text = autoCadAvailable
            ? "检测到 CAD。导出前会核对宿主插件版本，并在需要时提示安装或修复。"
            : "未检测到支持的 CAD，可在设置 → CAD 与环境中检查；也可直接使用本机写回。";
        if (!autoCadAvailable)
        {
            // Offline is the only reachable mode, so the radio group would be a choice that is not
            // a choice. Hide it and retitle the dialog, but keep it open: the temporary export
            // directory option below is a real decision that offline exports still need.
            ModeSection.Visibility = Visibility.Collapsed;
            HeaderText.Text = "选择导出目录";
            SubtitleText.Text = "未检测到支持的 CAD，将使用本机直接写回。云端 AI 翻译仍需登录并联网。";
        }
        Loaded += (_, _) => SetScrim(Visibility.Visible);
        Closed += (_, _) => SetScrim(Visibility.Collapsed);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    private void SetScrim(Visibility visibility) { if ((Owner as MainWindow)?.FindName("ModalScrim") is FrameworkElement scrim) scrim.Visibility = visibility; }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (TemporaryDirectoryOption.IsChecked == true)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择本批次临时导出目录" };
            if (dialog.ShowDialog(this) != true) return;
            TemporaryDirectory = Path.GetFullPath(dialog.FolderName);
        }
        SelectedMode = CadOption.IsChecked == true ? ExportMode.AutoCAD : ExportMode.Offline;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { SelectedMode = ExportMode.Cancel; DialogResult = false; }
}
