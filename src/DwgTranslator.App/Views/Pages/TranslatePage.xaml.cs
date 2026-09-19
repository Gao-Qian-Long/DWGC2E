using System.Windows.Controls;

namespace DwgTranslator.App.Views.Pages;

/// <summary>翻译工作区：左侧队列与右侧设置使用独立滚动表面，避免嵌套页面滚动。</summary>
public partial class TranslatePage : UserControl
{
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
    }
}
