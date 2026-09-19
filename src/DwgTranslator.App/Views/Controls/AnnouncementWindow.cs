using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

public sealed class AnnouncementWindow : Window
{
    public AnnouncementWindow(Window? owner, string content)
    {
        Title = "公告中心"; Owner = owner; Width = 560;
        // §弹窗排版：高度随公告长度收敛。此前固定 Height=400，内容只有「暂无公告」时正文与按钮之间留大片空白。
        SizeToContent = SizeToContent.Height;
        MinWidth = 320; MinHeight = 240; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Surface");
        SetResourceReference(ForegroundProperty, "Brush.TextPrimary");
        FontFamily = owner?.FontFamily ?? new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        FontSize = 14;
        var text = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, LineHeight = 25 };
        var close = new Button { Content = "我知道了", MinWidth = 96, IsDefault = true, IsCancel = true };
        close.SetResourceReference(StyleProperty, "Button.Primary");
        close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        actions.Children.Add(close);
        Content = new DialogShell("公告中心", text, actions);
        DialogShell.Constrain(this);
        // 必须在 Constrain 之后：它把 MaxHeight 抬到整个工作区，否则长公告会撑到满屏。
        MaxHeight = Math.Min(SystemParameters.WorkArea.Height * 0.75, 720);
    }
}
