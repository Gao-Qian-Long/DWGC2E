using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DwgTranslator.App.Views.Controls;

public partial class TitleBarControl : UserControl
{
    // Single square while normal, two overlapping squares while maximized, matching the
    // convention every other window follows.
    private static readonly Geometry RestoreGlyph =
        Geometry.Parse("M 2,0 L 10,0 L 10,8 M 0,2 L 8,2 L 8,10 L 0,10 Z");

    private static readonly Geometry MaximizeGlyphGeometry =
        Geometry.Parse("M 0,0 L 10,0 L 10,10 L 0,10 Z");

    public TitleBarControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window window)
            {
                window.StateChanged += (_, _) => UpdateMaximizeGlyph(window.WindowState);
                UpdateMaximizeGlyph(window.WindowState);
            }
        };
    }

    private void UpdateMaximizeGlyph(WindowState state)
    {
        if (MaximizeGlyph == null) return;
        bool maximized = state == WindowState.Maximized;
        MaximizeGlyph.Data = maximized ? RestoreGlyph : MaximizeGlyphGeometry;
        MaxRestoreButton.ToolTip = maximized ? "向下还原" : "最大化";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window window)
            window.WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window window)
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window window)
            window.Close();
    }
}
