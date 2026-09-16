using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace DwgTranslator.App.Views;
/// <summary>One themed confirmation surface. The owner supplies the dimmed scrim.</summary>
public sealed class PromptDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.Cancel;
    private PromptDialog(string message, string title, MessageBoxButton buttons, string? confirmText, string? rejectText, string? cancelText)
    {
        Title = title; Width = 460; MaxHeight = 600; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Brush.Surface");
        var body = new StackPanel { Margin = new Thickness(24) };
        var heading = new DockPanel();
        var close = new Button { Content = "×", Width = 34, Height = 34, ToolTip = "取消并关闭", Style = (Style)Application.Current.FindResource("Button.Tertiary") };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        heading.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,16,0,20) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string text, MessageBoxResult result, bool primary = false)
        {
            var b = new Button { Content = text, MinWidth = 84, Margin = new Thickness(8,0,0,0), IsDefault = primary, Style = (Style)Application.Current.FindResource(primary ? "Button.Primary" : "Button.Secondary") };
            b.Click += (_, _) => { _result = result; DialogResult = true; }; actions.Children.Add(b);
        }
        if (buttons is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel) Add(cancelText ?? "取消", MessageBoxResult.Cancel);
        if (buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel) { Add(rejectText ?? (buttons == MessageBoxButton.YesNoCancel ? "放弃更改" : "否"), MessageBoxResult.No); Add(confirmText ?? (buttons == MessageBoxButton.YesNoCancel ? "保存更改" : "确定"), MessageBoxResult.Yes, true); }
        else Add("确定", MessageBoxResult.OK, true);
        body.Children.Add(actions); Content = new Border { BorderBrush = (Brush)Application.Current.FindResource("Brush.BorderLight"), BorderThickness = new Thickness(1), Child = body };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }
    public static MessageBoxResult Show(string message, string title = "提示", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        string? confirmText = null, string? rejectText = null, string? cancelText = null)
    {
        var owner = Application.Current.MainWindow;
        var scrim = (owner as MainWindow)?.FindName("ModalScrim") as FrameworkElement;
        if (scrim != null) scrim.Visibility = Visibility.Visible;
        try { var dialog = new PromptDialog(message, title, buttons, confirmText, rejectText, cancelText) { Owner = owner }; dialog.ShowDialog(); return dialog._result; }
        finally { if (scrim != null) scrim.Visibility = Visibility.Collapsed; }
    }
}