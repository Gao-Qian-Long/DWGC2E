using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views;

/// <summary>
/// 主题化的确认对话框（§29）：替代散落在各处的 <c>MessageBox.Show(..., YesNo)</c>，
/// 保证确认框与深浅色令牌一致，按钮文本也明确（"清空" / "继续" 而不是笼统的"是/否"）。
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmText, bool danger, string cancelText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        Title = title;

        if (!danger)
            ConfirmButton.Style = (Style)FindResource("Button.Primary");
    }

    /// <summary>弹确认框；返回 true 表示用户点了确认。</summary>
    public static bool Ask(Window? owner, string title, string message,
        string confirmText = "确定", bool danger = true, string cancelText = "取消")
    {
        var dialog = new ConfirmDialog(title, message, confirmText, danger, cancelText) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
