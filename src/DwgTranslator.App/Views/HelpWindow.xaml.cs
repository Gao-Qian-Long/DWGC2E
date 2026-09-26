using System.Windows;

namespace DwgTranslator.App.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        // §自适应（2026-09-25）：MinWidth=620 在窄屏上会顶出工作区；尺寸上下限收敛进工作区，
        // 帮助正文本来就在 HelpPanel 的 ScrollViewer 里，收窄后仍可读。Esc 已由本类自己处理，
        // 所以这里只用 ConstrainSize（不重复挂 Esc）。
        Controls.DialogShell.ConstrainSize(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
