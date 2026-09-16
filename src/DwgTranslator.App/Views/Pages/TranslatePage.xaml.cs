using System.Windows.Controls;

namespace DwgTranslator.App.Views.Pages;

/// <summary>TranslatePage — 设计稿中的一个页面，由 MainWindow 的侧栏切换。</summary>
public partial class TranslatePage : UserControl
{
    private void QueueMenu_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu == null) return;
        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void UpdateWorkspaceHeight()
    {
        QueueAreaRow.Height = new System.Windows.GridLength(System.Math.Clamp(TranslationScroll.ActualHeight - 340, QueueWorkspace.IsVisible ? 200 : 160, 330));
    }

    public TranslatePage()
    {
        InitializeComponent();
        TranslationScroll.SizeChanged += (_, _) => UpdateWorkspaceHeight();
        QueueWorkspace.IsVisibleChanged += (_, _) => UpdateWorkspaceHeight();
    }
}