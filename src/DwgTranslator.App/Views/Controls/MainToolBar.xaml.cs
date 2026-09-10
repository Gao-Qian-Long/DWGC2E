using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DwgTranslator.App.Views.Controls;

public partial class MainToolBar : UserControl
{
    public MainToolBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the button's menu on a left click. WPF only opens a ContextMenu on right click, and a
    /// split button (click to act, arrow for the menu) has no built-in control.
    /// </summary>
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.IsOpen = true;
    }
}
