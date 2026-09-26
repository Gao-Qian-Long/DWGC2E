using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Pages;

/// <summary>翻译工作区：左侧队列与右侧设置使用独立滚动表面，避免嵌套页面滚动。</summary>
public partial class TranslatePage : UserControl
{
    private bool? _workspaceStacked;

    private void QueueMenu_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu == null) return;
        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    public TranslatePage()
    {
        InitializeComponent();
        TranslationWorkspace.SizeChanged += (_, _) => UpdateWorkspaceColumns();
        Loaded += (_, _) => UpdateWorkspaceColumns();
    }

    private void UpdateWorkspaceColumns()
    {
        var width = TranslationWorkspace.ActualWidth;
        if (width <= 0) return;
        var breakpoint = TryFindResource("Size.BreakpointPageStack") is double value ? value : 1110d;
        var stacked = Math.Round(width) < breakpoint;
        if (_workspaceStacked == stacked) return;
        _workspaceStacked = stacked;

        // Moving the settings panel below the queue is only half a reflow: the old 400-DIP
        // column must also stop reserving space, otherwise the queue is clipped to a half-page
        // strip and the entire right half of its row remains empty.
        SettingsWorkspaceColumn.MinWidth = stacked ? 0 : 360;
        SettingsWorkspaceColumn.Width = new GridLength(stacked ? 0 : 400);
        QueueWorkspaceColumn.MinWidth = stacked ? 0 : 520;
        Grid.SetColumnSpan(QueueSurface, stacked ? 2 : 1);
    }
}
