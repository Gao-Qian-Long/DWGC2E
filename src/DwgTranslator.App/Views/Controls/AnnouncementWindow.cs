using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DwgTranslator.Core.Api;

namespace DwgTranslator.App.Views.Controls;

/// <summary>
/// The announcement center: one window for both channels (site-wide notice + the signed-in user's
/// directed notifications). The site-wide path keeps its exact previous behaviour; the directed
/// section is additive and collapses to nothing when the user has no directed notifications.
/// </summary>
public sealed class AnnouncementWindow : Window
{
    /// <summary>Site-wide announcement only — unchanged shape, still the path used whenever the user
    /// has no directed notifications (signed out, unreachable backend, or an empty feed).</summary>
    public AnnouncementWindow(Window? owner, string content)
        : this(owner, content, null) { }

    /// <param name="directed">
    /// The signed-in user's own notifications, or null/empty. Read state is NOT decided here: the bell
    /// marks notifications read when it opens this window (it owns the ViewModel), so the window stays a
    /// pure view and never starts a network call of its own.
    /// </param>
    public AnnouncementWindow(Window? owner, string content, IReadOnlyList<DirectedNotification>? directed)
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

        var body = new StackPanel();
        var items = directed?.Where(x => x != null).ToList() ?? new List<DirectedNotification>();
        if (items.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "发送给你的通知", Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold });
            foreach (var item in items) body.Children.Add(BuildDirectedItem(item));
            // The site-wide notice is a different channel; separate the two so the user can tell which
            // text came from the operator's public notice and which was addressed to them personally.
            var divider = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 16), Opacity = 0.5 };
            divider.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
            body.Children.Add(divider);
        }

        body.Children.Add(new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, LineHeight = 25 });

        var close = new Button { Content = "我知道了", MinWidth = 96, IsDefault = true, IsCancel = true };
        close.SetResourceReference(StyleProperty, "Button.Primary");
        close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        actions.Children.Add(close);
        Content = new DialogShell("公告中心", body, actions);
        DialogShell.Constrain(this);
        // 必须在 Constrain 之后：它把 MaxHeight 抬到整个工作区，否则长公告会撑到满屏。
        MaxHeight = Math.Min(SystemParameters.WorkArea.Height * 0.75, 720);
    }

    private static StackPanel BuildDirectedItem(DirectedNotification item)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Title) ? "通知" : item.Title,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock { Text = item.Body, TextWrapping = TextWrapping.Wrap, LineHeight = 22 });

        var meta = new List<string>();
        if (item.CreatedAt is DateTime created) meta.Add(created.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        // Unread items are labelled explicitly so "打开即已读" is verifiable on screen, not only in state.
        if (item.IsUnread) meta.Add("未读");
        if (meta.Count > 0)
        {
            var stamp = new TextBlock { Text = string.Join(" · ", meta), Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
            stamp.SetResourceReference(TextBlock.ForegroundProperty, item.IsUnread ? "Brush.Danger" : "Brush.TextSecondary");
            panel.Children.Add(stamp);
        }
        return panel;
    }
}
