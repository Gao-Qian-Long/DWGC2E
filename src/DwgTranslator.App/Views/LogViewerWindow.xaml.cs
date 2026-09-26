using System.Windows;
using DwgTranslator.App.ViewModels;

namespace DwgTranslator.App.Views;

public partial class LogViewerWindow : Window
{
    public LogViewerWindow(LogViewModel viewModel)
    {
        InitializeComponent();
        // §自适应（2026-09-25）：MinWidth=700 / 固定 1060 宽在窄屏上会被工作区截住；
        // 收敛尺寸上下限（日志列表自己带滚动）。Esc 已由本类处理，故只用 ConstrainSize。
        Controls.DialogShell.ConstrainSize(this);
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            viewModel.IsVisible = true;
            viewModel.RefreshEntriesCommand.Execute(null);
        };
        Closed += (_, _) => viewModel.IsVisible = false;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }
}
