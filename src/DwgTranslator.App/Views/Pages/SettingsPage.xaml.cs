using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Services;
namespace DwgTranslator.App.Views.Pages;
public partial class SettingsPage : UserControl
{
    public SettingsPage() { InitializeComponent(); SizeChanged += (_, _) => UpdateLayoutMode(); Loaded += (_, _) => UpdateLayoutMode(); AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_,_) => Dispatcher.BeginInvoke(new Action(UpdateSaveState)))); AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,new RoutedEventHandler((_,_)=>Dispatcher.BeginInvoke(new Action(UpdateSaveState)))); AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,new RoutedEventHandler((_,_)=>UpdateSaveState())); AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,new SelectionChangedEventHandler((_,_)=>Dispatcher.BeginInvoke(new Action(UpdateSaveState)))); IsVisibleChanged += (_,_)=>UpdateSaveState(); Loaded += (_, _) => { if (DataContext is MainViewModel vm) { EnvironmentHost.Content = new EnvironmentCheckPanel(vm); vm.ValidateSettingsInputs = () => !HasValidationError(this); vm.NotifySettingsDraftState(!HasValidationError(this)); } }; LiveLogAutoScroll(); }
    // 实时日志列表：新条目到达时自动滚到底部，保持"正在运行"的滚动观感（设置-日志分区，2026-10-02）。
    // Items（ItemCollection）自身实现 INotifyCollectionChanged，绑定晚到也不影响挂钩。
    //
    // 必须节流：日志高频流入时，逐条 ScrollIntoView 会触发布局失效风暴，其频率远高于渲染帧率，
    // 让 Dispatcher 永远到不了 ApplicationIdle 优先级——UI 冒烟在设置日志分区可见 + 翻译进行中
    // （日志持续流入）时实测挂死（matrix 阶段 IsProcessing=True，2026-10-02）。
    // 注意：仅靠 IsVisible 守卫无效，因为冒烟/用户恰好在日志分区"可见"时才触发风暴。
    // 解法：CollectionChanged 只置脏标记，由 300ms DispatcherTimer（Background 优先级）统一滚动，
    // 把滚动频率与日志频率解耦；tick 之间队列可排空到 ApplicationIdle，等待不再被饿死。
    private System.Windows.Threading.DispatcherTimer? _liveLogScrollTimer;
    private volatile bool _liveLogScrollPending;
    private void LiveLogAutoScroll()
    {
        if (LiveLogList == null) return;
        ((System.Collections.Specialized.INotifyCollectionChanged)LiveLogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && e.NewItems is { Count: > 0 })
                _liveLogScrollPending = true;
        };
        _liveLogScrollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
            // DispatcherTimer 默认即 Background 优先级（高于 ApplicationIdle）；300ms 的间隔让
            // tick 之间留出空闲窗口，低优先级等待（冒烟的 Dispatcher.InvokeAsync(ApplicationIdle)）得以完成。
        };
        _liveLogScrollTimer.Tick += (_, _) =>
        {
            if (!_liveLogScrollPending) return;
            _liveLogScrollPending = false;
            if (LiveLogList.IsVisible && LiveLogList.IsLoaded && LiveLogList.Items.Count > 0)
                LiveLogList.ScrollIntoView(LiveLogList.Items[^1]);
        };
        _liveLogScrollTimer.Start();
        LiveLogList.Loaded += (_, _) => { _liveLogScrollTimer?.Start(); if (LiveLogList.IsVisible && LiveLogList.Items.Count > 0) LiveLogList.ScrollIntoView(LiveLogList.Items[^1]); };
        LiveLogList.Unloaded += (_, _) => { _liveLogScrollTimer?.Stop(); };
    }
    private void UpdateLayoutMode()
    {
        var compact = Controls.ResponsiveLayout.GetIsCompact(this);
        SettingsNav.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactSections.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        // G 主从双卡片的窄窗降级：左卡与槽列一起收为 0，右卡独占整行（等价于上下堆叠时"只剩右卡"），不破裂。
        // 两个宽度都从资源令牌读、不在代码里写死数值：Size.SettingsNav(168) / Size.CardGutter(16)。
        // 左卡要整张 Collapsed 而不只把列宽归零——列宽为 0 时卡片仍参与测量，1px 边框会被裁成一条残线。
        // CompactSections 在 XAML 里位于右列而非左卡内，所以折叠左卡不会连带隐藏它
        // （RefinementSmoke.cs:34 与 :385 断言它的 IsVisible 必须严格等于 compact）。
        SettingsNavCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (TryFindResource("Size.SettingsNav") is GridLength settingsNavWidth)
            SettingsNavColumn.Width = compact ? new GridLength(0) : settingsNavWidth;
        if (TryFindResource("Size.CardGutter") is GridLength cardGutter)
            SettingsNavGutter.Width = compact ? new GridLength(0) : cardGutter;
        // 页头与「关于」分区在窄窗让位给下拉选择器，内容与操作条不再需要水平偏移。
        var compactAbout = compact && DataContext is MainViewModel { SettingsSection: 5 };
        WorkspaceHeader.Visibility = compactAbout ? Visibility.Collapsed : Visibility.Visible;
        if (AboutUpdateColumn != null && AboutUpdatePanel != null && AboutUpdateCard != null)
        {
            // S5 · D7/D8：更新状态块已从身份卡内部移到独立的 AboutUpdateCard（Grid 列 2，槽列 1 为 Size.CardGutter）。
            // 因此行/列归属改设在**卡片**上——AboutUpdatePanel 现在是卡片内的 StackPanel，
            // 对它调用 Grid.SetColumn 已无意义（不再是 Grid 的直接子元素）。
            // 中等宽度也上下堆叠：固定宽度的更新卡会挤窄身份卡，常见 1280–1440 窗口尤其明显。
            // 与下方快捷入口/版本详情共用 1256 DIP 阈值，使关于页在同一宽度切换为纵向版式。
            var aboutCardsStack = compact || ActualWidth < 1256;
            var aboutUpdateWidth = TryFindResource("Size.AboutUpdateColumn") as GridLength? ?? new GridLength(236);
            AboutUpdateColumn.Width = aboutCardsStack ? new GridLength(0) : aboutUpdateWidth;
            Grid.SetColumn(AboutUpdateCard, aboutCardsStack ? 0 : 2);
            Grid.SetColumnSpan(AboutUpdateCard, aboutCardsStack ? 3 : 1);
            Grid.SetRow(AboutUpdateCard, aboutCardsStack ? 1 : 0);
            AboutUpdateCard.Margin = aboutCardsStack ? new Thickness(0, 12, 0, 0) : new Thickness(0);
            // 堆叠时身份卡也跨满 3 列、槽列收 0，否则右边会留一条 16 DIP 的空槽（两卡宽度不一致）。
            if (AboutIdentityCard != null) Grid.SetColumnSpan(AboutIdentityCard, aboutCardsStack ? 3 : 1);
            if (AboutIdentityGutter != null) AboutIdentityGutter.Width = aboutCardsStack ? new GridLength(0) : (TryFindResource("Size.CardGutter") as GridLength? ?? new GridLength(16));
            AboutUpdatePanel.Orientation = aboutCardsStack ? Orientation.Horizontal : Orientation.Vertical;
            AboutStatusPanel.Margin = aboutCardsStack ? new Thickness(0,0,14,0) : new Thickness(0,0,0,10);
            AboutUpdatePanel.Margin = aboutCardsStack ? new Thickness(0,10,20,0) : new Thickness(0);
            AboutUpdatePanel.HorizontalAlignment = aboutCardsStack ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }
        // §G 副作用收敛（必须与上面新增的左卡配套看）：左卡 + 槽列固定占去 168 + 16 = 184 DIP，
        // 于是右卡在 1280~1440 这些主流笔记本宽度下比改前窄 184 DIP；窗口 ≥1528 时右卡已被
        // SettingsContent 的 MaxWidth=1080 封顶，这 184 被完全吸收、不受影响。
        // 分区 06 的「快捷入口 / 版本详情」并排布局需要约 974 DIP 内容宽，才能让快捷入口说明
        // "查看软件使用说明和常见操作"（13 字 × FontSize.Secondary 13 = 169 DIP）单行放得下。
        // 实测说明可用宽（改前 → 改后）：1280 → 173 / 81；1366 → 205 / 124；1440 → 205 / 161。
        // 不足时说明会被 CharacterEllipsis 截成 6~12 字，而 1366×768 正是 UI 冒烟的 matrix 截图尺寸，
        // 视觉复审必然看到。处置沿用本任务契约点名的手段（"现有响应式机制…可上下堆叠"）：
        // 把既有 compact 堆叠分支的触发条件从"仅 compact"放宽到"内容宽不足"，
        // 于是绝大多数宽度下并排 / 堆叠的分界与文字是否被截断一致，不新增任何布局代码。
        // 阈值换算（t7 按 smoke 实测修正——t3 推算时误把 Spacing.Page 的左右 64 也算进了本控件宽度）：
        //   实测 window.Width=1280 时本页 ActualWidth=1080.0，故 ActualWidth = 窗口宽 - 侧栏 200；
        //   Spacing.Page 的左右各 32 是页面**内部** Page Grid 的 Margin，已在 ActualWidth 之内，不能再减一次。
        //   链：Col2 = ActualWidth - 64 - 184；SettingsContent = min(Col2, 1080)；inner = SettingsContent - 42；
        //       说明可用宽 = (inner - 396 - 16) / 2 - 30 - 70 - 8 = (inner - 412) / 2 - 108，需 ≥ 169
        //       （396 = 版本详情列 380 + 槽 16；30 = About.ShortcutCard Padding 14×2 + 边框 2；
        //         70 = 格内 cols 50/*/20 的两侧；8 = 中层 StackPanel 的 Spacing.Inline 右距）。
        //   ⇒ 完全不截断需 inner ≥ 966 ⇔ ActualWidth ≥ 1256 ⇔ 窗口宽 ≥ 1456
        //     （t3 写的 974 / 1200 / 1464 中，974 是把 1280 改前的可用 inner 误当成了需求值，实际只需 966）。
        //   以 ActualWidth < 1256 作为堆叠阈值，窗口宽约 <1456 时统一进入堆叠态；
        //   这样 1280、1366、1440 三档都不会把快捷入口说明截断，1920 及更宽窗口仍保持并排布局。
        // 不会自激振荡：堆叠只改内容高度，ActualWidth 由 PageHost 决定；SettingsScroll 的纵向滚动条
        //          只影响 SettingsContent 宽度、不影响本 UserControl 的 ActualWidth，故不会反复触发 SizeChanged。
        // AboutDetails* 与上方身份/更新卡共用同一堆叠阈值；UpdateNotificationSmoke.cs:29-30
        // 断言的 AboutUpdatePanel / AboutUpdateButton 命名及绑定行为不变。
        // **更正（S5 实测，替换改前此处的一句错误陈述）**：改前注释称"tests\ 全库 grep 证实无任何断言
        // 引用 AboutDetailsCard / AboutDetailsColumn / AboutDetailsGap"，该陈述**为假**——
        // tests\DwgTranslator.App.UiSmoke\Program.cs:764-766 明确按 x:Name 取用 AboutDetailsCard，
        // 并断言其在 1280 窄窗下 Grid.GetRow==2 && Grid.GetColumn==0（堆叠槽 row2）。四个快捷入口按钮确实无断言引用。
        // 因此 AboutDetailsCard 的 x:Name 与本分支的行/列归属都是**被冒烟守住的契约**，不可删改。
        // （该错误陈述经 git show HEAD: 溯源，来自上一批的提交 c334969，非本批引入。）
        var aboutStack = compact || ActualWidth < 1256;
        if (AboutDetailsCard != null && AboutDetailsColumn != null && AboutDetailsGap != null)
        {
            // 快捷入口与版本详情并排铺满横向空间；横向空间不足时落回堆叠，避免两张卡都被挤到不可读。
            // 数值一律从资源令牌读（Size.CardGutter / Size.AboutDetailsColumn），不再在代码里写第二份字面量。
            var detailsGap = TryFindResource("Size.CardGutter") as GridLength? ?? new GridLength(16);
            var detailsColumn = TryFindResource("Size.AboutDetailsColumn") as GridLength? ?? new GridLength(380);
            AboutDetailsGap.Width = aboutStack ? new GridLength(0) : detailsGap;
            AboutDetailsColumn.Width = aboutStack ? new GridLength(0) : detailsColumn;
            Grid.SetRow(AboutDetailsCard, aboutStack ? 2 : 1);
            Grid.SetColumn(AboutDetailsCard, aboutStack ? 0 : 2);
            Grid.SetColumnSpan(AboutDetailsCard, aboutStack ? 3 : 1);
            AboutDetailsCard.Margin = aboutStack ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        }
    }
    private void SettingsSection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != sender) return;
        SettingsScroll?.ScrollToTop();
        UpdateLayoutMode();
    }
    private void NamingChanged(object sender, TextChangedEventArgs e) { Dispatcher.BeginInvoke(new Action(()=> { if(NamingPreview!=null) PreviewNaming_Click(this,new RoutedEventArgs()); })); }
    private void BrowseCad_Click(object sender, RoutedEventArgs e) { var d=new Microsoft.Win32.OpenFolderDialog(); if(d.ShowDialog()==true && DataContext is MainViewModel vm) { vm.SettingsDraft.AutoCadInstallPath=d.FolderName; DataContext=null; DataContext=vm; UpdateSaveState(); } }
    private void BrowsePlugin_Click(object sender, RoutedEventArgs e) { var d=new Microsoft.Win32.OpenFileDialog { Filter="CAD 插件 (*.dll)|*.dll" }; if(d.ShowDialog()==true && DataContext is MainViewModel vm) { vm.SettingsDraft.CadPluginPath=d.FileName; DataContext=null; DataContext=vm; UpdateSaveState(); } }
    private void UpdateSaveState() { if (DataContext is MainViewModel vm) vm.NotifySettingsDraftState(!HasValidationError(this)); }
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFolderDialog(); if (d.ShowDialog() == true && DataContext is MainViewModel vm) { vm.SettingsDraft.ExportDirectory = d.FolderName; GetBindingExpression(DataContextProperty)?.UpdateTarget(); DataContext = null; DataContext = vm; UpdateSaveState(); } }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) vm.OpenFolder(vm.LogDirectory); }
    private void PreviewNaming_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) { try { NamingPreview.Text = new OutputPathResolver(vm.SettingsDraft).RenderFileName("总图.dwg", vm.CurrentTargetLang, DateTime.Now); } catch { NamingPreview.Text = "命名规则无效。"; } } }
    private static bool HasValidationError(DependencyObject root) { if (Validation.GetHasError(root)) return true; for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) if (HasValidationError(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) return true; return false; }
}
