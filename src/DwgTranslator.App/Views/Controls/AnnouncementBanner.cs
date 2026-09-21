using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using Serilog;
namespace DwgTranslator.App.Views.Controls;

/// <summary>Title-bar announcement bell: unread red dot + short static caption; click opens the announcement center.</summary>
public sealed class AnnouncementBanner : Border
{
    // §L21 铃铛公告：跑马灯移除——原实现把公告全文塞进滚动 ticker，Canvas 排版还让文字
    // 中线比铃铛低约 4 DIP（用户反馈"铃铛和文字没有对齐"）。新交互（用户指定）：
    // 未读公告 → 铃铛右上角红点 + 右侧静态"有新公告"；点开即已读 → 恢复简短文案；
    // 全文只在公告中心窗口展示。DisplayText 保持全文语义（冒烟断言依赖），短文案在 UpdateTicker 派生。
    private readonly TextBlock _ticker = new()
    {
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Button _button;
    private CancellationTokenSource? _request;
    private Window? _owner;
    private AnnouncementWindow? _dialog;
    private DateTime _lastAttempt = DateTime.MinValue;
    private string _content = "";
    public string DisplayText { get; private set; } = "正在读取公告…";
    public bool HasUnread => _dot.Visibility == Visibility.Visible;
    public bool IsOpen { get => _dialog?.IsVisible == true; set { if (value) OpenAnnouncement(); else _dialog?.Close(); } }

    public AnnouncementBanner()
    {
        // §L20 顶栏列宽 1120 起可收缩：公告条由固定 220 改为 160..320 弹性宽度
        // §A1 文字下缘被切平（用户批注「文字下面被截断了」）的根因是高度约束打架：
        //     Button.Base 用样式设了 MinHeight=36，而尺寸约束永远压过本地 Height，
        //     所以本地 Height=32 无效——按钮实际 36 高，从 32 高的容器里溢出 4 DIP，
        //     居中的文字被推到容器下缘之外。现在容器/按钮/行网格取同一个确定高度 34，
        //     并给按钮同时钉死 MinHeight 与 MaxHeight（只钉 Height 仍会被样式里的 MinHeight=36 顶掉），
        //     12px 字形在 34 的行里上下各有约 8 DIP 余量，字体回退（Inter 缺失→Microsoft YaHei，
        //     行盒更高）也不会再切到下缘。34 仍在 44 的标题栏行内居中（y=5..39），
        //     冒烟的 HTCLIENT/HTCAPTION 命中点（banner 本地 16,16 与窗口 240/350 列）不受影响。
        Height = 34; MinWidth = 160; MaxWidth = 320; Background = Brushes.Transparent;
        _button = new Button { Width = double.NaN, Height = 34, MinHeight = 34, MaxHeight = 34, Padding = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = "查看公告" };
        // §A2 悬停/聚焦跳动：Button.Icon 继承 Button.Base 的模板，IsMouseOver 给模板里的 Bd 挂
        //     TranslateTransform Y=-2（整块抬起 2 DIP，就是用户看到的"跳一下"），IsKeyboardFocused
        //     还把 BorderThickness 从 0 改成 2 —— 那是真实的尺寸变化，点过一次后文字永久内缩。
        //     Button.TitleBarIcon 的模板只改背景色与透明度，不动任何几何属性，天然满足
        //     "MouseOver 触发器不得改变自身/父容器尺寸"。
        //     另外：悬停浮层本来就是不占布局槽位的 ToolTip（WPF ToolTip 即 Popup，
        //     _ticker.ToolTip 承载公告全文），不需要再改成手写 Popup。
        _button.SetResourceReference(StyleProperty, "Button.TitleBarIcon");
        // §L21 对齐：行网格与两个子元素都显式垂直居中——铃铛(列0)与文字(列1)共用同一条中线，
        // 不再依赖 ContentPresenter 对 Canvas 自然高度的隐式排布。
        // §A1 行网格也给确定高度：文字所在单元格恒为 34，不再依赖"内容自然高度刚好塞得下"。
        var row = new Grid { MinWidth = 140, Height = 34, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Grid { Width = 20, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        var bell = new System.Windows.Shapes.Path
        {
            Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M 3,13 L 3,7 C 3,0 15,0 15,7 L 15,13 L 17,15 L 1,15 Z M 7,18 Q 9,20 11,18"),
        };
        bell.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.TextSecondary");
        _dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Danger");
        icon.Children.Add(bell); icon.Children.Add(_dot); row.Children.Add(icon);
        _ticker.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _ticker.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_ticker, 1); row.Children.Add(_ticker);
        _button.Content = row; Child = _button;
        _button.Click += (_, _) => OpenAnnouncement();
        ShowMessage(DisplayText);
        Loaded += async (_, _) =>
        {
            _owner = Window.GetWindow(this);
            if (_owner != null) _owner.Activated += OwnerActivated;
            _refresh.Start(); await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _refresh.Stop();
            if (_owner != null) _owner.Activated -= OwnerActivated;
            _owner = null; _request?.Cancel(); _dialog?.Close();
        };
        DataContextChanged += async (_, _) => { if (IsLoaded) await RefreshAsync(); };
        // Only the timer is "automatic"; it is the one that honours the directed back-off.
        _refresh.Tick += async (_, _) => await RefreshAsync(automatic: true);
    }
    private void OpenAnnouncement()
    {
        if (_dialog != null) { _dialog.Activate(); return; }
        var vm = DataContext as MainViewModel;
        // Directed notifications are part of the same center (用户已确认不新做消息中心), so the window
        // receives both channels and marks both read on open (CONTRACT-notifications.md §8).
        _dialog = new AnnouncementWindow(Window.GetWindow(this), _content.Length > 0 ? _content : DisplayText, vm?.DirectedNotifications);
        _dialog.Closed += (_, _) => { _dialog = null; _button.Focus(); };
        _dialog.Show();
        if (vm == null) return;

        var unreadIds = vm.DirectedNotifications.Where(x => x.IsUnread).Select(x => x.Id).ToList();
        if (unreadIds.Count > 0)
        {
            // Fired and observed, never awaited: the window is already visible, and the ViewModel queues a
            // failed report for replay on the next refresh, so a dead backend cannot block or disturb the UI.
            _ = ReportDirectedReadAsync(vm, unreadIds);
        }
        if (_content.Length > 0) vm.MarkAnnouncementRead(_content);

        UpdateUnreadState(vm); UpdateTicker(); UpdateAccessibleName();
    }

    private async Task ReportDirectedReadAsync(MainViewModel vm, IReadOnlyList<string> ids)
    {
        try { await vm.MarkDirectedNotificationsReadAsync(ids); }
        catch (Exception ex) { Log.Debug("定向通知已读回传异常：{ErrorType}", ex.GetType().Name); }
    }

    private async void OwnerActivated(object? sender, EventArgs e)
    { if (DateTime.UtcNow - _lastAttempt > TimeSpan.FromSeconds(15)) await RefreshAsync(); }

    public async Task RefreshAsync() => await RefreshAsync(automatic: false);

    /// <param name="automatic">
    /// True for the one-minute timer. Only the timer honours the directed-notification back-off: an
    /// explicit refresh (window activated, account changed, test) always asks, so a backend that came
    /// back is picked up immediately instead of waiting out the remaining back-off.
    /// </param>
    public async Task RefreshAsync(bool automatic)
    {
        if (!IsLoaded || DataContext is not MainViewModel vm) return;
        _request?.Cancel();
        using var request = new CancellationTokenSource();
        _request = request; _lastAttempt = DateTime.UtcNow;

        // Directed notifications first: they ride the account session through vm->_apiClient and must
        // never go out on the anonymous site client below. A failure is folded into a status by the
        // ViewModel, so the site-wide refresh still runs and the bell keeps its public caption.
        await RefreshDirectedAsync(vm, request.Token, automatic);

        try
        {
            var value = await vm.ReadSiteAnnouncementAsync(request.Token);
            if (!request.IsCancellationRequested)
            {
                _content = value;
                UpdateUnreadState(vm);
                ShowMessage(string.IsNullOrWhiteSpace(value) ? "暂无公告" : value);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested)
            {
                // Only the site-wide channel is cleared: directed notifications are still valid content
                // from a different endpoint and must survive a public-endpoint outage.
                _content = ""; UpdateUnreadState(vm);
                ShowMessage("公告暂时无法加载，稍后自动重试");
                Log.Debug("公告加载失败：{ErrorType}", ex.GetType().Name);
            }
        }
        finally { if (ReferenceEquals(_request, request)) _request = null; }
    }

    /// <summary>
    /// Pulls the signed-in user's directed notifications into the single red dot. Never throws: the
    /// ViewModel returns a status instead of an exception, and every non-success status leaves the
    /// previous snapshot (or emptiness) untouched.
    /// </summary>
    private async Task RefreshDirectedAsync(MainViewModel vm, CancellationToken cancellationToken, bool automatic)
    {
        try
        {
            if (automatic && !vm.ShouldPollDirectedNotifications) return;
            await vm.RefreshDirectedNotificationsAsync(cancellationToken);
            UpdateUnreadState(vm);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Belt and braces: the ViewModel already converts every failure into a status, but the title
            // bar must not be the component that introduces an unhandled exception.
            Log.Debug("定向通知刷新异常：{ErrorType}", ex.GetType().Name);
        }
    }

    /// <summary>The red dot reflects either channel: directed unread state or the site-wide notice.</summary>
    private void UpdateUnreadState(MainViewModel vm)
    {
        var unread = vm.HasUnreadDirectedNotifications || (_content.Length > 0 && vm.IsAnnouncementUnread(_content));
        _dot.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowMessage(string value)
    {
        DisplayText = value;
        UpdateTicker();
        UpdateAccessibleName();
    }
    // 标题栏只放短状态文案：未读→"有新公告"；已读且有内容→短公告原文或"查看公告"；
    // 无内容（暂无公告/正在读取/加载失败）→ DisplayText 本身。全文放 ToolTip 兜底。
    // 站点公告为空但有定向通知时，标题栏必须仍有可读文案，否则"未读红点"旁边是一片空白。
    private void UpdateTicker()
    {
        _ticker.Text = string.IsNullOrWhiteSpace(_content) && DataContext is MainViewModel { HasUnreadDirectedNotifications: true }
            ? "有新通知"
            : HasUnread && _content.Length > 0 ? "有新公告"
            : string.IsNullOrWhiteSpace(_content) ? DisplayText
            : DisplayText.Length <= 20 ? DisplayText : "查看公告";
        _ticker.ToolTip = DisplayText;
    }
    private void UpdateAccessibleName() => System.Windows.Automation.AutomationProperties.SetName(_button, (HasUnread ? "有未读公告，" : "查看公告，") + DisplayText);
}
