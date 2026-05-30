using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DwgTranslator.App.Views;

/// <summary>
/// Dialog for choosing DWG export mode: AutoCAD precise writeback vs offline writeback.
/// </summary>
public partial class ExportModeDialog : Window
{
    public enum ExportMode { AutoCAD, Offline, Cancel }

    public ExportMode SelectedMode { get; private set; } = ExportMode.Cancel;

    public ExportModeDialog(bool autoCadAvailable)
    {
        InitializeComponent();
        UpdateAutoCadStatus(autoCadAvailable);
    }

    private void UpdateAutoCadStatus(bool available)
    {
        if (available)
        {
            AutoCadStatusText.Text = "状态：AutoCAD 正在运行，可用";
            AutoCadStatusText.Foreground = Brushes.Green;
            AutoCadCard.IsEnabled = true;
            AutoCadCard.Opacity = 1.0;
        }
        else
        {
            AutoCadStatusText.Text = "状态：未检测到 AutoCAD，不可用";
            AutoCadStatusText.Foreground = Brushes.Red;
            AutoCadCard.IsEnabled = false;
            AutoCadCard.Opacity = 0.5;
            // Auto-select offline card visually
            SelectCard(OfflineCard);
        }
    }

    private void AutoCadCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (!AutoCadCard.IsEnabled) return;
        SelectedMode = ExportMode.AutoCAD;
        DialogResult = true;
        Close();
    }

    private void OfflineCard_Click(object sender, MouseButtonEventArgs e)
    {
        SelectedMode = ExportMode.Offline;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = ExportMode.Cancel;
        DialogResult = false;
        Close();
    }

    private void SelectCard(Border card)
    {
        AutoCadCard.BorderBrush = new SolidColorBrush(Colors.LightGray);
        AutoCadCard.BorderThickness = new Thickness(1);
        OfflineCard.BorderBrush = new SolidColorBrush(Colors.LightGray);
        OfflineCard.BorderThickness = new Thickness(1);

        card.BorderBrush = new SolidColorBrush(Color.FromRgb(25, 118, 210)); // #1976D2
        card.BorderThickness = new Thickness(2);
    }
}
