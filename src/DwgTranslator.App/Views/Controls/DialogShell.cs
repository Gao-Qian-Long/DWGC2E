using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

/// <summary>Shared modal composition: fixed identity/actions, independent scrollable body and local feedback.</summary>
public sealed class DialogShell : DockPanel
{
    public DialogShell(string title, UIElement body, UIElement actions)
    {
        Margin = new Thickness(24);
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,16) };
        heading.SetResourceReference(StyleProperty, "SectionTitleStyle");
        SetDock(heading, Dock.Top); Children.Add(heading);
        SetDock(actions, Dock.Bottom); Children.Add(actions);
        var feedback = new ToastHost { IsScoped = true, MaximumVisible = 1 };
        SetDock(feedback, Dock.Bottom); Children.Add(feedback);
        Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }
    public static void Constrain(Window window)
    {
        window.MaxWidth = SystemParameters.WorkArea.Width;
        window.MaxHeight = SystemParameters.WorkArea.Height;
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { window.Close(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(window, window.Title);
    }
}
