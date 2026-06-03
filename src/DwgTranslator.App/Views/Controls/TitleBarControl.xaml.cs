using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

public partial class TitleBarControl : UserControl
{
    public TitleBarControl()
    {
        InitializeComponent();
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
