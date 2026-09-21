using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace DwgTranslator.App.Views;

/// <summary>
/// Interaction logic for MainWindow
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<string, FrameworkElement> _pageCache = new(StringComparer.Ordinal);
    private bool? _isCompactLayout;
    /// <summary>§L5 断点：进入 compact 的客户区宽度上限，必须等于 MinWidth 才可达。</summary>
    private const double CompactEnterWidth = 1120;
    /// <summary>§L5 迟滞上限：已进入 compact 后放宽到 1136，避免侧栏在边界上反复收放。</summary>
    private const double CompactLeaveWidth = 1136;

    // ── §D5 最大化必须贴合"工作区"而不是"整块屏幕" ──────────────────────────────
    // WindowStyle=None + WindowChrome 的窗口没有系统非客户区，WPF 自带的 WindowChromeWorker
    // 只负责命中测试与调整边框，它不处理 WM_GETMINMAXINFO；于是 DefWindowProc 基于
    // rcMonitor（监视器全高）计算最大化矩形，底部 30 DIP 状态栏被任务栏盖住。
    // 修法：在 SourceInitialized 挂 HwndSourceHook，把 ptMaxPosition/ptMaxSize 改写成 rcWork。
    // 机制已用独立探针复核（不靠推断）：WPF 的 WindowChromeWorker 不会把该消息 handled 置真，
    // 因此后加的钩子确实能收到并生效 —— 未挂钩时最大化到 (-11,-11)+2582x1622（底部溢出 81 px），
    // 挂钩后为 (0,0)+2560x1530，与 rcWork 完全一致、四边偏差均为 0。
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // The viewport is measured in DIP. No fixed minimum canvas and no global scale transform.
    private void PageViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // WPF stretch/grid layout owns the content viewport. Imperative Width/Height assignments
        // caused feedback loops when drawers or scrollbars changed the available size.
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (PageHost == null) return;
        // 用客户区宽度而不是 Window.ActualWidth：无边框窗口的 ActualWidth 会包含
        // WindowChrome 的不可见调整边框，导致 MinWidth=1120 时读到 1120+边框宽，
        // 阈值判定永远差几 DIP。
        var viewportWidth = Content is FrameworkElement shell && shell.ActualWidth > 0 ? shell.ActualWidth : ActualWidth;
        // §L5 窗口下限已改为 1120×640：阈值必须与下限对齐，否则 compact 分支永不可达
        // （原 null 分支 <1100 在 MinWidth=1280 下是死代码）。
        // 两个陷阱：① 判定必须读旧状态、写回在后面，否则读到的是自己刚写进去的值；
        // ② 窗口宽度经 DPI 换算带浮点尾差（1120 读到 1120.0000000000002），
        //    先吸附到整 DIP 再比较，否则窗口正好停在边界时永远判不中。
        viewportWidth = Math.Round(viewportWidth);
        var wasCompact = _isCompactLayout;
        var compact = wasCompact switch
        {
            null => viewportWidth <= CompactEnterWidth,
            false => viewportWidth <= CompactEnterWidth,
            true => viewportWidth <= CompactLeaveWidth
        };
        _isCompactLayout = compact;
        PageHost.MaxWidth = ActualWidth >= 1900 ? 1600 : double.PositiveInfinity;
        Controls.ResponsiveLayout.SetIsCompact(this, compact);
        // IsShort 与宽度无关，只按高度判定（同样先吸附整 DIP，避开 DPI 尾差）。
        Controls.ResponsiveLayout.SetIsShort(this, Math.Round(ActualHeight) < 640);
        // 展开宽度只有 Size.Sidebar 一个来源（Themes/Metrics.xaml）：这里再写死一个数字的话，
        // 令牌改动后窗口一在 compact 边界来回切换就会跳回旧宽度。
        SidebarColumn.Width = compact ? new GridLength(64) : (GridLength)FindResource("Size.Sidebar");
        Resources["Spacing.Page"] = compact ? new Thickness(20) : new Thickness(32,24,32,24);
        ShellStatusLabel.MaxWidth = compact ? 180 : 420;
        ShellProgressLabel.MaxWidth = compact ? 100 : 260;
        ShellVersionLabel.MaxWidth = compact ? 100 : 120;
        BrandLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarBrand.Margin = new Thickness(16, compact ? 12 : 24, 0, compact ? 12 : 24);
        AccountEntryLabels.Visibility = AccountChevron.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AccountEntryBorder.Margin = compact ? new Thickness(4,0,4,4) : new Thickness(16,0,16,12);
        AccountEntryContent.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 14);
        foreach (System.Windows.Controls.ListBoxItem item in NavList.Items)
        {
            item.Padding = new Thickness(compact ? 14 : 12,0,0,0);
            item.Margin = new Thickness(compact ? 0 : 8,2,compact ? 0 : 8,2);
            if (item.Content is System.Windows.Controls.StackPanel panel)
                foreach (var child in panel.Children)
                    if (child is System.Windows.Controls.TextBlock text) { text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; item.ToolTip = text.Text; }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) => UpdateResponsiveLayout();
        var area = SystemParameters.WorkArea;
        // §L5 窗口下限不能大于屏幕工作区：125% 缩放的 1366×768（约 1093×530 DIP）上
        // 否则会得到"必然大于屏幕、且拖不小"的窗口。仍保留 800×560 的可用下界。
        MinWidth = Math.Min(MinWidth, Math.Max(800, area.Width - 20));
        MinHeight = Math.Min(MinHeight, Math.Max(560, area.Height - 20));
        Width = Math.Min(Math.Max(Width, MinWidth), area.Width);
        Height = Math.Min(Math.Max(Height, MinHeight), area.Height);

        // §D5 上面的 SystemParameters.WorkArea 只反映主显示器，且发生在句柄创建之前。
        // 把最大化真正约束到"窗口所在显示器的工作区"必须在 SourceInitialized 里做，共两步：
        // ① 按窗口实际所在显示器把 MinWidth/MinHeight 收敛进工作区 —— 否则窗口下限会盖过
        //    ptMaxSize：探针实测在本机（175% 缩放，rcWork 高 874.3 DIP）把 MinHeight 设为
        //    900 DIP 时，钩子写入的 ptMaxSize 被 WPF 自身下限覆盖，底部仍溢出 45 px；
        //    把 MinHeight 放宽到工作区高度以内后溢出回到 0.0（探针 variant E）。
        //    注意 ctor 里的 area.Height - 20 兜底在 1366×768@175% 下仍会得到 560 > 530 DIP，
        //    所以这一收敛必须按显示器重做。
        // ② 挂 WM_GETMINMAXINFO 钩子把最大化矩形改写成 rcWork。
        SourceInitialized += OnSourceInitialized;

        // Resolve ViewModel from DI container (falls back to parameterless ctor if DI not ready)
        // 依赖全部由 DI 装配（MainViewModel 只有这一个构造函数）：容器没起来就是致命错误，
        // 不能悄悄退回"半装配"的另一套实例——那样界面会看不到任务层，翻译按钮会静默失效。
        _viewModel = App.Services?.GetService<MainViewModel>()
            ?? throw new InvalidOperationException("服务容器未初始化，无法创建主视图模型。");
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ShowActivePage();

        // BUG FIX: 异步初始化术语库加载，避免 UI 线程同步阻塞
        // 操作结果统一走 Toast（§29）
        Services.ToastService.Attach(Toasts);
        Toasts.ShouldPause = () => _viewModel.IsTermDrawerOpen || _viewModel.IsTaskDetailOpen;

        Loaded += OnLoaded;
        // MainViewModel 是单例（与任务管理器同生命周期）：关窗只解除本窗口的订阅并落盘工作区会话，
        // 不再 Dispose 整个 VM——否则第二次打开窗口会拿到一个已取消 CTS、已退订事件的残废实例。
        Closing += (s, e) =>
        {
            if (!_viewModel.ConfirmLeavePage()) { e.Cancel = true; return; }
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.SaveWorkspaceSession();
            _viewModel.DetachWindowScopedState();
        };
    }


    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) ShowActivePage();
    }

    // §D5 工作区收敛 + 最大化矩形钩子（见 ctor 顶部注释的两步说明）。
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        if (TryGetWorkArea(handle, out var work))
        {
            // rcWork 是物理像素，MinWidth/MinHeight 是 DIP，必须换算后再比较。
            var source = HwndSource.FromHwnd(handle);
            var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (scaleX > 0 && scaleY > 0)
            {
                var workWidthDip = (work.Right - work.Left) / scaleX;
                var workHeightDip = (work.Bottom - work.Top) / scaleY;
                // 只在工作区装不下当前下限时才收敛，正常显示器上是空操作（不动 1120×640 契约）。
                if (workWidthDip < MinWidth) MinWidth = Math.Max(640, workWidthDip);
                if (workHeightDip < MinHeight) MinHeight = Math.Max(480, workHeightDip);
            }
        }

        HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
    }

    private static bool TryGetWorkArea(IntPtr handle, out NativeRect work)
    {
        work = default;
        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        work = info.rcWork;
        return true;
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        if (!TryGetWorkArea(hwnd, out var work)) return IntPtr.Zero;

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.ptMaxPosition.X = work.Left;
        info.ptMaxPosition.Y = work.Top;
        info.ptMaxSize.X = work.Right - work.Left;
        info.ptMaxSize.Y = work.Bottom - work.Top;
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void ShowActivePage()
    {
        if (PageHost == null) return;
        var key = _viewModel.CurrentPage;
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = key switch
            {
                MainViewModel.PageBatch => new Pages.BatchTasksPage(),
                MainViewModel.PageGlossary => new Pages.GlossaryPage(),
                MainViewModel.PageSettings => new Pages.SettingsPage(),
                MainViewModel.PageAccount => new Pages.AccountPage(),
                _ => new Pages.TranslatePage()
            };
            _pageCache[key] = page;
        }
        if (!ReferenceEquals(PageHost.Content, page)) PageHost.Content = page;
    }
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (control && e.Key == Key.S)
        {
            CommitFocusedEditor();
            e.Handled = true;
            await _viewModel.HandleSaveShortcutAsync();
            return;
        }

        if (control && e.Key == Key.O)
        {
            e.Handled = true;
            if (IsEditingInput())
            {
                Services.ToastService.Info("正在编辑内容，请先完成编辑再导入图纸。");
                return;
            }
            await _viewModel.HandleImportShortcutAsync();
            return;
        }

        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (IsEditingInput())
            {
                Services.ToastService.Info("正在编辑内容，F5 不会启动翻译。");
                return;
            }
            await _viewModel.HandleRunShortcutAsync();
        }
    }

    private static bool IsEditingInput() => Keyboard.FocusedElement is TextBoxBase or PasswordBox
        || FindAncestor<DataGridCell>(Keyboard.FocusedElement as DependencyObject) != null;

    private static void CommitFocusedEditor()
    {
        if (Keyboard.FocusedElement is TextBox textBox)
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        var grid = FindAncestor<DataGrid>(Keyboard.FocusedElement as DependencyObject);
        if (grid == null) return;
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : null;
        }
        return null;
    }

    /// <summary>右上角用户入口：用主题化 ContextMenu 承载账号/置顶/设置等入口（§9）。</summary>
    private void GlobalAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsAccountLoggedIn) { _viewModel.LoginAccountCommand.Execute(null); return; }
        if (sender is System.Windows.Controls.Button b && b.ContextMenu != null) b.ContextMenu.DataContext = _viewModel;
        AccountMenu_Click(sender, e);
    }

    private void AccountMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null) return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.HorizontalOffset = -8;
        button.ContextMenu.IsOpen = true;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        _viewModel.StartUpdateChecks();
        await ImportStartupFilesAsync();

        // A freshly installed copy is started with --env-check so the user lands on the screen that
        // finishes the setup (installing the CAD plugin) instead of an empty workspace.
        if (App.OpenEnvironmentCheckOnStart)
        {
            App.ClearEnvironmentCheckRequest();
            _viewModel.ShowEnvironmentCheckCommand.Execute(null);
        }
    }

    /// <summary>
    /// Imports files passed on the command line (for example "Open with" from Explorer or a
    /// shortcut). They share the exact code path used by drag-and-drop.
    /// </summary>
    private async Task ImportStartupFilesAsync()
    {
        var startupFiles = App.StartupFiles;
        if (startupFiles.Length == 0) return;

        App.ClearStartupFiles();
        await _viewModel.ImportDroppedFilesAsync(startupFiles);
    }

    // ───────────────────────── Drag & drop ─────────────────────────

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedFiles(e.Data, out var files))
        {
            // Not a file drop: leave it alone so text selection drags inside the grid keep working.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        ShowDropOverlay(files);
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!TryGetDroppedFiles(e.Data, out var files)) return;

        e.Handled = true;
        await _viewModel.ImportDroppedFilesAsync(files);
    }

    /// <summary>
    /// Reads a file-drop payload. Returns false for any other payload so the window does not
    /// claim drags it cannot handle.
    /// </summary>
    private static bool TryGetDroppedFiles(IDataObject? data, out string[] files)
    {
        files = [];
        if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return false;

        files = dropped;
        return true;
    }

    private void ShowDropOverlay(string[] files)
    {
        DropOverlay.Visibility = Visibility.Visible;

        if (files.Length == 0)
        {
            DropOverlayFiles.Visibility = Visibility.Collapsed;
            return;
        }

        var accepted = files.Count(MainViewModel.IsSupportedFile);
        var preview = string.Join("、", files.Take(3).Select(Path.GetFileName));
        if (files.Length > 3) preview += " …";

        DropOverlayFiles.Visibility = Visibility.Visible;
        DropOverlayFiles.Text = accepted == files.Length
            ? Strings.Get("DropHintFiles", files.Length, preview)
            : Strings.Get("DropHintFilesPartial", accepted, files.Length);
    }
}
