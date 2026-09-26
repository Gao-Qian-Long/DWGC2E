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
    private bool _closeValidationPending;
    private bool _closeAfterValidation;
    // ── §自适应 侧栏两态的唯一判据：图标栏能摆下页面，展开栏要等到"摆得下两栏" ────────────
    // 页面可用宽 = 窗口宽 − 侧栏 − Spacing.Page 左右（展开 −248、图标 −96）。
    // 翻译页两栏并排需要页宽 ≥ Size.BreakpointPageStack(1110)：左栏队列表固定列 680 + 右栏 400 +
    // 槽 16 + 边框/滚动条 14。低于它页面自己会折成上下（ResponsiveLayout.StackBelow），所以
    //   · 图标栏（−96）只要窗口 ≥ 1206 就能并排；
    //   · 展开栏（−248）要窗口 ≥ 1358 才能并排。
    // 因此把"收起侧栏"一直用到 1358：在 1024–1357 这一段用 64 DIP 图标栏把宽度全留给页面，
    // 页面照样能并排；到 1358 以上才把标签列展开。窗口下限 1024 是用户 2026-09-25 的要求
    // （"拖到 1024 甚至更窄，每个页面按宽度重排"），由页面重排保证不裁切。
    /// <summary>进入紧凑布局的客户区宽度上限：展开侧栏摆不下翻译页两栏的宽度（1110 + 248）。</summary>
    private const double CompactEnterWidth = 1358;
    /// <summary>
    /// 退出紧凑布局的迟滞上限，避免窗口在边界上反复收放侧栏（16 DIP 迟滞）。
    /// 必须与展开阈值拉开：若迟滞阈值等于判定边界，侧栏收放会随 DPI 尾差来回翻转
    /// （上一轮实测 SidebarToastStabilitySmoke 的某一档因此从 56 DIP 图标栏跳回 168 DIP 展开条）。
    /// </summary>
    private const double CompactLeaveWidth = 1374;

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
        // WindowChrome 的不可见调整边框会让窗口边界宽度产生几 DIP 偏移，
        // 阈值判定永远差几 DIP。
        var viewportWidth = Content is FrameworkElement shell && shell.ActualWidth > 0 ? shell.ActualWidth : ActualWidth;
        // §自适应（2026-09-25）外壳只负责"侧栏两态 + 页面外边距"；页面内部的折行/收列由页面自己的
        // 宽度断点决定（ResponsiveLayout / ResponsiveTable 附加属性）。所以这里不再需要"下限之上必须
        // 展开"这种对齐关系：1024–1357 走图标栏把宽度让给页面，1358 以上才展开标签列（推导见
        // CompactEnterWidth 的注释）。绝不能让侧栏在页面还没准备好之前才展开——那会在展开的瞬间压窄页面。
        // 两个陷阱：① 判定必须读旧状态、写回在后面，否则读到的是自己刚写进去的值；
        // ② 窗口宽度经 DPI 换算带浮点尾差，
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
        // All pages share the same shell edges, including on maximized monitors.  A
        // centered shell cap made account/settings look like floating narrow islands
        // while translation and task tables filled the viewport.  Individual controls
        // may remain bounded, but the page canvas itself must always fill the host.
        PageHost.MaxWidth = double.PositiveInfinity;
        PageHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        Controls.ResponsiveLayout.SetIsCompact(this, compact);
        // IsShort 与宽度无关，只按高度判定（同样先吸附整 DIP，避开 DPI 尾差）。
        Controls.ResponsiveLayout.SetIsShort(this, Math.Round(ActualHeight) < 640);
        // 展开宽度只有 Size.Sidebar 一个来源（Themes/Metrics.xaml）：这里再写死一个数字的话，
        // 令牌改动后窗口一在 compact 边界来回切换就会跳回旧宽度。
        SidebarColumn.Width = compact ? new GridLength(64) : (GridLength)FindResource("Size.Sidebar");
        Resources["Spacing.Page"] = compact ? new Thickness(16) : new Thickness(24,16,24,16);
        ShellStatusLabel.MaxWidth = compact ? 180 : 420;
        ShellProgressLabel.MaxWidth = compact ? 100 : 260;
        ShellVersionLabel.MaxWidth = compact ? 100 : 120;
        BrandLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarBrand.Margin = compact ? new Thickness(0, 12, 0, 12) : new Thickness(16, 24, 0, 24);
        SidebarBrand.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        SidebarBrandIcon.Margin = compact ? new Thickness(0) : (Thickness)FindResource("Spacing.InlineWide");
        AccountEntryLabels.Visibility = AccountChevron.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AccountEntryBorder.Margin = compact ? new Thickness(4,0,4,4) : new Thickness(16,0,16,12);
        AccountEntryContent.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        AccountEntryContent.Width = compact ? 28 : double.NaN;
        AccountEntryContent.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        AccountEntryContent.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 14);
        foreach (System.Windows.Controls.ListBoxItem item in NavList.Items)
        {
            item.Padding = compact ? new Thickness(0) : new Thickness(12,0,0,0);
            item.Margin = new Thickness(compact ? 0 : 8,2,compact ? 0 : 8,2);
            if (item.Content is System.Windows.Controls.StackPanel panel)
            {
                panel.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                foreach (var child in panel.Children)
                {
                    if (child is System.Windows.Controls.TextBlock text)
                    {
                        text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                        if (!string.IsNullOrWhiteSpace(text.Text)) item.ToolTip = text.Text;
                    }
                    else if (child is System.Windows.Shapes.Path icon)
                        icon.Margin = compact ? new Thickness(0) : (Thickness)FindResource("Spacing.InlineWide");
                }
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) => UpdateResponsiveLayout();
        var area = SystemParameters.WorkArea;
        // §L5 窗口下限不能大于屏幕工作区：高 DPI / 小屏幕上
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
        Closing += OnClosingAsync;
    }

    private async void OnClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_closeAfterValidation)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.SaveWorkspaceSession();
            _viewModel.DetachWindowScopedState();
            return;
        }

        e.Cancel = true;
        if (_closeValidationPending) return;
        _closeValidationPending = true;
        try
        {
            if (!await _viewModel.ConfirmLeavePageAsync()) return;
            _closeAfterValidation = true;
            // WPF still marks the current Closing event as in-progress until this async-void
            // handler returns, even when the awaited validation already completed synchronously.
            // Queue the second Close so it starts a fresh close cycle instead of throwing
            // InvalidOperationException from Window.VerifyNotClosing().
            Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
        }
        finally { _closeValidationPending = false; }
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
                // 只在工作区装不下当前下限时才收敛，普通显示器保持 1024×640；
                // 小工作区（更窄时）仍进入 compact 布局，并把窗口下限限制在工作区内 ——
                // 窗口宽于工作区是比"内容被压窄"更糟的故障，所以这条兜底不能被下限顶掉。
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
        // WindowChrome can otherwise leave the native minimum track size at the value
        // captured before DPI/monitor negotiation.  Set it explicitly on every query so
        // dragging cannot continue below the responsive layout's supported floor.
        var source = HwndSource.FromHwnd(hwnd);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (scaleX > 0 && scaleY > 0)
        {
            info.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * scaleX);
            info.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * scaleY);
        }
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
        if (!TryGetDroppedFiles(e.Data, out var files) || !_viewModel.CanImportFiles)
        {
            // Never advertise a copy drop that the import layer will reject. During translation,
            // export or account switching the cursor stays "not allowed" and the overlay stays hidden.
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
        if (!_viewModel.CanImportFiles)
        {
            Services.ToastService.Warning(_viewModel.IsLoggingIn
                ? "账户正在切换，请完成后再导入图纸。"
                : "当前任务正在执行，完成或取消后再导入图纸。");
            return;
        }
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
