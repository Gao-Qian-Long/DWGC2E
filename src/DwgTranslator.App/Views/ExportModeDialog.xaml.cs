using System.Windows;
using System.Windows.Input;
namespace DwgTranslator.App.Views;
public partial class ExportModeDialog : Window
{
    public enum ExportMode { AutoCAD, Offline, Cancel }
    public ExportMode SelectedMode { get; private set; } = ExportMode.Cancel;
    public ExportModeDialog(bool autoCadAvailable)
    {
        InitializeComponent();
        CadOption.IsEnabled = autoCadAvailable;
        CadOption.IsChecked = autoCadAvailable;
        OfflineOption.IsChecked = !autoCadAvailable;
        CadStatus.Text = autoCadAvailable ? "CAD 环境可用。" : "CAD 环境不可用，可在设置 → CAD 与环境中检查。";
        Loaded += (_, _) => SetScrim(Visibility.Visible);
        Closed += (_, _) => SetScrim(Visibility.Collapsed);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    private void SetScrim(Visibility visibility) { if ((Owner as MainWindow)?.FindName("ModalScrim") is FrameworkElement scrim) scrim.Visibility = visibility; }
    private void Confirm_Click(object sender, RoutedEventArgs e) { SelectedMode = CadOption.IsChecked == true ? ExportMode.AutoCAD : ExportMode.Offline; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { SelectedMode = ExportMode.Cancel; DialogResult = false; }
}
