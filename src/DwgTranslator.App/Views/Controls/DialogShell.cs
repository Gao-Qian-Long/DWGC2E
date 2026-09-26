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
    /// <summary>
    /// §自适应（2026-09-25）：把窗口的尺寸上下限都收敛进当前工作区。
    /// 窄屏 / 高 DPI 下，对话框各自写死的 MinWidth（如支付窗 900、云端术语 600）会把窗口下限顶到工作区
    /// 之外——那时窗口既拖不小、内容也滚不动，右侧直接被屏幕切掉。MaxWidth/MaxHeight 只保证"不会比屏幕
    /// 宽"，管不住下限，所以下限也必须一起收敛，溢出交给内容自己的滚动宿主承担。
    /// 只在确实超出时才收紧，宽屏下逐字不变（这就是"界面显示要一致"）。
    /// 供已经自己处理 Esc 的窗口单独复用（见 HelpWindow / LogViewerWindow / LanguagePairDialog）。
    /// </summary>
    public static void ConstrainSize(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
        window.MinWidth = Math.Min(window.MinWidth, Math.Max(320, area.Width - 48));
        window.MinHeight = Math.Min(window.MinHeight, Math.Max(240, area.Height - 48));
    }

    public static void Constrain(Window window)
    {
        ConstrainSize(window);
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { window.Close(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(window, window.Title);
    }
}
