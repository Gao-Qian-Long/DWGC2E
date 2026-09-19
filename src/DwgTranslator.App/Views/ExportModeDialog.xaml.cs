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
        if (isTranslation) OfflineHint.Text = "离线写回按在线模式的 30% 消耗字符额度（不是套餐价格打折）。AI 翻译仍需联网。同一任务重试沿用首次计费模式；导出已有译文不重复扣费。";
        CadOption.IsEnabled = autoCadAvailable;
        CadOption.IsChecked = autoCadAvailable;
        OfflineOption.IsChecked = !autoCadAvailable;
        CadStatus.Text = autoCadAvailable ? "CAD 环境可用。" : "CAD 环境不可用，可在设置 → CAD 与环境中检查。";
        if (!autoCadAvailable)
        {
            // Offline is the only reachable mode, so the radio group would be a choice that is not
            // a choice. Hide it and retitle the dialog, but keep it open: the temporary export
            // directory option below is a real decision that offline exports still need.
            ModeSection.Visibility = Visibility.Collapsed;
            HeaderText.Text = "选择导出目录";
            SubtitleText.Text = "CAD 环境不可用，将使用离线导出。可在设置 → CAD 与环境中检查。";
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
