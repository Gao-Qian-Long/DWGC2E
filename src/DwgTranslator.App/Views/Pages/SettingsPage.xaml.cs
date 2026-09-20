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
        LiveLogList.Loaded += (_, _) => { if (LiveLogList.IsVisible && LiveLogList.Items.Count > 0) LiveLogList.ScrollIntoView(LiveLogList.Items[^1]); };
        LiveLogList.Unloaded += (_, _) => { _liveLogScrollTimer?.Stop(); };
    }
    private void UpdateLayoutMode()
    {
        var compact = Controls.ResponsiveLayout.GetIsCompact(this);
        SettingsNav.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactSections.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        // 页面已无左侧导航列：分区切换条在窄窗让位给下拉选择器，内容与操作条不再需要水平偏移。
        var compactAbout = compact && DataContext is MainViewModel { SettingsSection: 5 };
        WorkspaceHeader.Visibility = compactAbout ? Visibility.Collapsed : Visibility.Visible;
        AboutSubtitle.Visibility = compactAbout ? Visibility.Collapsed : Visibility.Visible;
        if (AboutUpdateColumn != null && AboutUpdatePanel != null)
        {
            AboutUpdateColumn.Width = new GridLength(compact ? 0 : 236);
            Grid.SetColumn(AboutUpdatePanel, compact ? 1 : 2);
            Grid.SetRow(AboutUpdatePanel, compact ? 1 : 0);
            Grid.SetRowSpan(AboutUpdatePanel, compact ? 1 : 2);
            AboutUpdatePanel.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
            AboutStatusPanel.Margin = compact ? new Thickness(0,0,14,0) : new Thickness(0,0,0,10);
            AboutUpdatePanel.Margin = compact ? new Thickness(0,10,20,0) : new Thickness(0);
            AboutUpdatePanel.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }
        if (AboutDetailsCard != null && AboutDetailsColumn != null && AboutDetailsGap != null)
        {
            // 快捷入口与版本详情并排铺满横向空间；窄窗落回堆叠，避免两张卡都被挤到不可读。
            AboutDetailsGap.Width = new GridLength(compact ? 0 : 16);
            AboutDetailsColumn.Width = new GridLength(compact ? 0 : 380);
            Grid.SetRow(AboutDetailsCard, compact ? 1 : 0);
            Grid.SetColumn(AboutDetailsCard, compact ? 0 : 2);
            Grid.SetColumnSpan(AboutDetailsCard, compact ? 3 : 1);
            AboutDetailsCard.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
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
