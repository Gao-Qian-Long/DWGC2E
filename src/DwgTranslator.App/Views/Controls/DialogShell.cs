using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

/// <summary>Shared modal composition: fixed identity/actions, independent scrollable body and local feedback.</summary>
public sealed class DialogShell : DockPanel
{
    /// <summary>单行 scoped Toast 的自然高度：Border Padding 12,8 + 14px 文本 + 8 底距。</summary>
    private const double FeedbackBandHeight = 44;
    private readonly Grid feedbackBand;
    private bool reserveFeedbackBand;

    /// <summary>
    /// §弹窗排版：预留反馈行高度。scoped Toast 出现/消失会把 Dock.Bottom 的操作区上下推动
    /// （分类管理弹窗实测 310 → 354，跳动 44 DIP）。置 true 后反馈区常驻占位，按钮组不动。
    /// 占位必须落在常驻可见的容器上：ToastHost 无提示时是 Visibility.Collapsed
    /// （ToastHost.xaml.cs:34/57），而 Collapsed 元素不参与布局，直接给 ToastHost 设 MinHeight 不生效。
    /// </summary>
    public bool ReserveFeedbackBand
    {
        get => reserveFeedbackBand;
        set { reserveFeedbackBand = value; feedbackBand.MinHeight = value ? FeedbackBandHeight : 0; }
    }

    public DialogShell(string title, UIElement body, UIElement actions)
    {
        Margin = new Thickness(24);
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,16) };
        heading.SetResourceReference(StyleProperty, "SectionTitleStyle");
        SetDock(heading, Dock.Top); Children.Add(heading);
        SetDock(actions, Dock.Bottom); Children.Add(actions);
        var feedback = new ToastHost { IsScoped = true, MaximumVisible = 1 };
        feedbackBand = new Grid(); feedbackBand.Children.Add(feedback);
        SetDock(feedbackBand, Dock.Bottom); Children.Add(feedbackBand);
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
